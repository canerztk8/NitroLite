using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Win32;

namespace NitroLite.Core;

public class PowerTransitionEventArgs : EventArgs
{
    public bool IsAc { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool ModeApplied { get; init; }
    public OperatingMode VerifiedMode { get; init; }
    public bool BoostApplied { get; init; }
    public CpuBoostMode VerifiedBoost { get; init; }
    public bool FanApplied { get; init; }
    public FanMode VerifiedFan { get; init; }
}

/// <summary>
/// Persisted snapshot of the AC desktop baseline while battery Eco is active, so a crash or forced exit on battery
/// can be undone on the next start.
/// </summary>
public class DcEcoRecoveryState
{
    public bool IsDcEcoActive { get; set; }
    public OperatingMode SavedMode { get; set; } = OperatingMode.Default;
    public CpuBoostMode SavedBoost { get; set; } = CpuBoostMode.Aggressive;
    public FanMode SavedFan { get; set; } = FanMode.Auto;
    public CpuBoostMode? SavedDcBoost { get; set; }
    // Built-in panel refresh rate before Eco lowered it to 60 Hz (null = Eco did not change it).
    public int? SavedRefreshRate { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Orchestrates automated profile switching, CPU boost management, fan curves, and display refresh rates
/// upon AC adapter connection and disconnection.
/// Conforms to Requirement R1 & R4:
/// - DC Eco: Quiet mode, Disabled CPU boost, Auto fans, 60Hz display.
/// - DC Balanced: Retains current settings (NitroSense default behavior).
/// - AC Reconnect: Restores saved mode, CPU boost, fan profile, and switches display to 165Hz.
/// - Debounce: 500ms debounce protects against physical cord wiggling or contact bouncing.
/// - MUX Protection: Display switching uses User32 without touching GPU MUX or DDS registers.
/// </summary>
public class PowerAutomationService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus; // 0 = Offline (DC), 1 = Online (AC), 255 = Unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private readonly AcerAgentClient _agentClient;
    private readonly CpuPowerService _cpuPowerService;
    private readonly IDisplayRefreshRateService _displayService;
    private readonly AppSettings _settings;
    private SemaphoreSlim? _transitionLock;

    private readonly System.Timers.Timer _debounceTimer;
    private readonly object _stateSyncLock = new();
    private bool _lastAcStatus = true;
    private bool _hasEvaluatedInitialState = false;
    private bool _pendingDcApply;
    private DcPowerProfilePreference? _lastPreference;
    private bool? _lastRefreshPreference;
    private bool _isDcEcoActive = false;
    private bool _isDisposed = false;

    private bool _pendingAcModeRestore = false;
    private bool _pendingAcCpuRestore = false;
    private bool _pendingAcFanRestore = false;

    private OperatingMode _savedOperatingModeBeforeDc = OperatingMode.Default;
    private CpuBoostMode _savedCpuBoostBeforeDc = CpuBoostMode.Aggressive;
    private FanMode _savedFanModeBeforeDc = FanMode.Auto;
    // The power plan's battery (DC) boost value before Eco replaced it. Eco only writes the DC value,
    // so the AC value is never touched and a crash on battery cannot cost performance on the adapter.
    private CpuBoostMode? _savedDcBoostBeforeEco;
    // Panel refresh rate Eco replaced with 60 Hz; only this value is restored on AC (null = Eco did not touch it).
    private int? _savedRefreshRateBeforeEco;

    /// <summary>
    /// Optional file used to persist the Eco baseline. Null disables persistence (unit tests).
    /// </summary>
    public string? EcoStateFilePath { get; set; }

    internal void PersistEcoState()
    {
        var path = EcoStateFilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // Also keep the file while a lowered panel rate still has to be restored (e.g. lid was closed on AC).
            if (!_isDcEcoActive && !HasPendingAcRestore && !_savedRefreshRateBeforeEco.HasValue)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            var state = new DcEcoRecoveryState
            {
                IsDcEcoActive = _isDcEcoActive || HasPendingAcRestore,
                SavedMode = _savedOperatingModeBeforeDc,
                SavedBoost = _savedCpuBoostBeforeDc,
                SavedFan = _savedFanModeBeforeDc,
                SavedDcBoost = _savedDcBoostBeforeEco,
                SavedRefreshRate = _savedRefreshRateBeforeEco,
                TimestampUtc = DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Without this file a crash on battery leaves the Eco settings applied with nothing to restore them
            // from, so the failure must be in the log even though the running session still recovers in memory.
            AppLog.Warn("Eco durumu kaydedilemedi", $"{path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads a persisted Eco baseline (left behind by a crash or exit on battery) so the next
    /// <see cref="EvaluatePowerStateAsync"/> on AC restores the desktop settings. Returns true if one was loaded.
    /// </summary>
    public bool RestorePersistedEcoState()
    {
        var path = EcoStateFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var state = JsonSerializer.Deserialize<DcEcoRecoveryState>(File.ReadAllText(path));
            if (state == null) return false;
            if (!state.IsDcEcoActive)
            {
                // Only a panel refresh rate is still waiting to be restored.
                if (state.SavedRefreshRate.HasValue)
                {
                    lock (_stateSyncLock) { _savedRefreshRateBeforeEco = state.SavedRefreshRate; }
                }
                return false;
            }
            lock (_stateSyncLock)
            {
                _savedOperatingModeBeforeDc = state.SavedMode;
                _savedCpuBoostBeforeDc = state.SavedBoost;
                _savedFanModeBeforeDc = state.SavedFan;
                _savedDcBoostBeforeEco = state.SavedDcBoost;
                _savedRefreshRateBeforeEco = state.SavedRefreshRate;
                _isDcEcoActive = true;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the pending-restore flags under the same lock the manual setters use. The transition lock serialises
    /// the operations, but the fields are also written from the UI thread (manual mode/boost/fan changes), so they
    /// need one lock of their own.
    /// </summary>
    private void SetPendingRestore(bool? mode = null, bool? cpu = null, bool? fan = null)
    {
        lock (_stateSyncLock)
        {
            if (mode.HasValue) _pendingAcModeRestore = mode.Value;
            if (cpu.HasValue) _pendingAcCpuRestore = cpu.Value;
            if (fan.HasValue) _pendingAcFanRestore = fan.Value;
        }
    }

    private void ClearEcoBaseline()
    {
        lock (_stateSyncLock)
        {
            _isDcEcoActive = false;
            _savedDcBoostBeforeEco = null;
        }
    }

    public bool IsDcEcoActive => _isDcEcoActive;
    public bool HasPendingAcRestore => _pendingAcModeRestore || _pendingAcCpuRestore || _pendingAcFanRestore;
    internal bool HasPendingPanelRestore => _lastAcStatus && _savedRefreshRateBeforeEco.HasValue;
    /// <summary>True while a battery Eco pass still has steps that failed and must be retried.</summary>
    public bool HasPendingDcApply => _pendingDcApply;
    public bool PendingAcModeRestore => _pendingAcModeRestore;
    public bool PendingAcCpuRestore => _pendingAcCpuRestore;
    public bool PendingAcFanRestore => _pendingAcFanRestore;

    public OperatingMode SavedOperatingMode => _savedOperatingModeBeforeDc;
    public CpuBoostMode SavedCpuBoost => _savedCpuBoostBeforeDc;
    public FanMode SavedFanMode => _savedFanModeBeforeDc;
    public bool LastAcStatus => _lastAcStatus;

    public SemaphoreSlim? TransitionLock
    {
        get => _transitionLock;
        set => _transitionLock = value;
    }

    public Func<bool>? IsGameSessionActive { get; set; }
    public Func<GameProfile?>? GetActiveGameProfile { get; set; }
    public Func<OperatingMode>? GetCurrentOperatingMode { get; set; }
    public Func<CpuBoostMode>? GetCurrentCpuBoost { get; set; }
    public Func<FanMode>? GetCurrentFanMode { get; set; }

    public void SetSavedAcBaseline(OperatingMode mode, CpuBoostMode boost, FanMode fan, bool force = false)
    {
        lock (_stateSyncLock)
        {
            if (!force && _isDcEcoActive)
            {
                // Do not corrupt previously saved AC desktop baseline when on DC Eco mode!
                return;
            }
            _savedOperatingModeBeforeDc = mode;
            _savedCpuBoostBeforeDc = boost;
            _savedFanModeBeforeDc = fan;
        }
        PersistEcoState();
    }

    public void SetDcEcoActive(bool active)
    {
        lock (_stateSyncLock)
        {
            _isDcEcoActive = active;
        }
        PersistEcoState();
    }

    public void UpdateManualOperatingMode(OperatingMode mode)
    {
        lock (_stateSyncLock)
        {
            _savedOperatingModeBeforeDc = mode;
            _pendingAcModeRestore = false;
            if (_lastAcStatus && !HasPendingAcRestore)
            {
                _isDcEcoActive = false;
            }
        }
        PersistEcoState();
    }

    public void UpdateManualCpuBoost(CpuBoostMode boost)
    {
        lock (_stateSyncLock)
        {
            _savedCpuBoostBeforeDc = boost;
            // A manual choice overrides the battery value captured before Eco; otherwise reconnecting AC
            // would write that stale value back over what the user just picked.
            _savedDcBoostBeforeEco = null;
            _pendingAcCpuRestore = false;
            if (_lastAcStatus && !HasPendingAcRestore)
            {
                _isDcEcoActive = false;
            }
        }
        PersistEcoState();
    }

    public void UpdateManualFanMode(FanMode fan)
    {
        lock (_stateSyncLock)
        {
            _savedFanModeBeforeDc = fan;
            _pendingAcFanRestore = false;
            if (_lastAcStatus && !HasPendingAcRestore)
            {
                _isDcEcoActive = false;
            }
        }
        PersistEcoState();
    }

    public event Action<PowerTransitionEventArgs>? PowerModeSwitched;

    public PowerAutomationService(
        AcerAgentClient agentClient,
        CpuPowerService cpuPowerService,
        IDisplayRefreshRateService displayService,
        AppSettings settings,
        bool attachSystemEvents = true,
        SemaphoreSlim? transitionLock = null)
    {
        _agentClient = agentClient ?? throw new ArgumentNullException(nameof(agentClient));
        _cpuPowerService = cpuPowerService ?? throw new ArgumentNullException(nameof(cpuPowerService));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _transitionLock = transitionLock;

        _lastAcStatus = QueryHardwareAcStatus();

        _debounceTimer = new System.Timers.Timer(500.0)
        {
            AutoReset = false
        };
        _debounceTimer.Elapsed += OnDebounceElapsed;

        if (attachSystemEvents)
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch { }
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            TriggerDebounce();
        }
    }

    public void TriggerDebounce()
    {
        if (_isDisposed) return;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async void OnDebounceElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_isDisposed) return;
        try
        {
            await EvaluatePowerStateAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"OnDebounceElapsed failed: {ex.Message}");
        }
    }

    public async Task EvaluatePowerStateAsync(bool? forceAcStatus = null)
    {
        bool isAc = forceAcStatus ?? QueryHardwareAcStatus();
        await ProcessPowerTransitionAsync(isAc);
    }

    public async Task ProcessPowerTransitionAsync(bool isAc)
    {
        if (_isDisposed) return;

        if (_transitionLock != null)
        {
            await _transitionLock.WaitAsync();
        }
        try
        {
            if (_isDisposed) return;
            if (isAc == _lastAcStatus && _hasEvaluatedInitialState && !HasPendingAcRestore && !HasPendingPanelRestore && !_pendingDcApply && _lastPreference == _settings.DcPowerMode && _lastRefreshPreference == _settings.AutoRefreshRateSwitching)
            {
                return;
            }

            _lastAcStatus = isAc;
            _hasEvaluatedInitialState = true;
            _lastPreference = _settings.DcPowerMode;
            _lastRefreshPreference = _settings.AutoRefreshRateSwitching;
            _pendingDcApply = false;

            if (!isAc)
            {
                // Transition to DC (On Battery)
                if (_settings.DcPowerMode == DcPowerProfilePreference.Eco)
                {
                    if (!_isDcEcoActive)
                    {
                        _savedOperatingModeBeforeDc = GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default;
                        _savedCpuBoostBeforeDc = GetCurrentCpuBoost?.Invoke() ?? _cpuPowerService.CurrentMode;
                        _savedFanModeBeforeDc = GetCurrentFanMode?.Invoke() ?? FanMode.Auto;
                        try { _savedDcBoostBeforeEco = _cpuPowerService.QueryBoostModeExact(false); }
                        catch { _savedDcBoostBeforeEco = null; }
                    }
                    lock (_stateSyncLock) { _isDcEcoActive = true; }
                    PersistEcoState();

                    bool modeOk = false;
                    try
                    {
                        modeOk = await _agentClient.SetOperatingModeAsync(OperatingMode.Quiet);
                    }
                    catch { modeOk = false; }

                    bool boostOk = false;
                    try
                    {
                        // Battery (DC) power plan value only: the AC value stays untouched.
                        boostOk = await _cpuPowerService.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: false, targetDc: true);
                    }
                    catch { boostOk = false; }

                    bool fanOk = false;
                    try
                    {
                        fanOk = await _agentClient.SetFanModeAsync(FanMode.Auto);
                    }
                    catch { fanOk = false; }

                    bool refreshOk = true;
                    if (_settings.AutoRefreshRateSwitching)
                    {
                        // Built-in panel only. Remember its rate so AC restores exactly that, not "maximum".
                        int before = _displayService.GetCurrentRefreshRate();
                        // 0 = no built-in panel right now (lid closed, external monitor only): there is nothing to
                        // lower, and treating it as a failure left Eco permanently "partial".
                        refreshOk = before <= 60 || _displayService.SetRefreshRate(60);
                        if (before > 60 && refreshOk)
                        {
                            _savedRefreshRateBeforeEco = before;
                            PersistEcoState();
                        }
                    }

                    _pendingDcApply = !modeOk || !boostOk || !fanOk || !refreshOk;
                    var failed = new System.Collections.Generic.List<string>();
                    if (!modeOk) failed.Add("Sessiz Mod");
                    if (!boostOk) failed.Add(Loc.T("CPU Boost (Devre Dışı)"));
                    if (!fanOk) failed.Add("Fan Modu (Auto)");
                    if (!refreshOk) failed.Add(Loc.T("60 Hz ekran yenileme hızı"));

                    string message = failed.Count == 0
                        ? (_settings.AutoRefreshRateSwitching
                            ? Loc.T("Pilde Çalışma (Eco Modu): Güç tasarrufu ve 60 Hz ekran ayarı etkin.")
                            : Loc.T("Pilde Çalışma (Eco Modu): Güç tasarrufu uygulandı."))
                        : Loc.T($"Pilde Çalışma (Kısmi Eco): Uygulanamayan ayarlar: {string.Join(", ", failed)}");

                    PowerModeSwitched?.Invoke(new PowerTransitionEventArgs
                    {
                        IsAc = false,
                        Message = message,
                        ModeApplied = modeOk,
                        VerifiedMode = modeOk ? OperatingMode.Quiet : (GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default),
                        BoostApplied = boostOk,
                        VerifiedBoost = boostOk ? CpuBoostMode.Disabled : _cpuPowerService.CurrentMode,
                        FanApplied = fanOk,
                        VerifiedFan = fanOk ? FanMode.Auto : (GetCurrentFanMode?.Invoke() ?? FanMode.Auto)
                    });
                }
                else
                {
                    // Balanced mode: retain active settings (NitroSense behavior)
                    lock (_stateSyncLock) { _isDcEcoActive = false; }
                    PowerModeSwitched?.Invoke(new PowerTransitionEventArgs
                    {
                        IsAc = false,
                        Message = Loc.T("Pilde Çalışma (Dengeli Mod): Mevcut donanım ayarları korundu."),
                        ModeApplied = true,
                        VerifiedMode = GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default,
                        BoostApplied = true,
                        VerifiedBoost = _cpuPowerService.CurrentMode,
                        FanApplied = true,
                        VerifiedFan = GetCurrentFanMode?.Invoke() ?? FanMode.Auto
                    });
                }
            }
            else
            {
                // Transition to AC (Adapter Connected)
                bool allRestored = true;
                bool restoreAttempted = false;
                var failed = new System.Collections.Generic.List<string>();

                bool modeOk = false;
                bool boostOk = false;
                bool fanOk = false;
                OperatingMode targetMode = _savedOperatingModeBeforeDc;
                CpuBoostMode targetBoost = _savedCpuBoostBeforeDc;
                FanMode targetFan = _savedFanModeBeforeDc;

                if (_isDcEcoActive || HasPendingAcRestore)
                {
                    restoreAttempted = true;
                    bool isGame = IsGameSessionActive?.Invoke() == true;
                    var activeGame = GetActiveGameProfile?.Invoke();

                    targetMode = (isGame && activeGame != null) ? activeGame.TargetMode : _savedOperatingModeBeforeDc;
                    targetBoost = (isGame && activeGame != null) ? activeGame.TargetCpuBoost : _savedCpuBoostBeforeDc;
                    targetFan = (isGame && activeGame != null) ? activeGame.TargetFanMode : _savedFanModeBeforeDc;

                    if (_pendingAcModeRestore || _isDcEcoActive)
                    {
                        try
                        {
                            modeOk = await _agentClient.SetOperatingModeAsync(targetMode);
                        }
                        catch { modeOk = false; }
                        SetPendingRestore(mode: !modeOk);
                        if (!modeOk) failed.Add(Loc.T("Çalışma Modu"));
                    }

                    if (_pendingAcCpuRestore || _isDcEcoActive)
                    {
                        try
                        {
                            boostOk = await RestoreBoostAfterEcoAsync(isGame && activeGame != null, targetBoost);
                        }
                        catch { boostOk = false; }
                        SetPendingRestore(cpu: !boostOk);
                        if (!boostOk) failed.Add("CPU Boost");
                        if (boostOk && !(isGame && activeGame != null))
                        {
                            // Eco never changed the AC value, so the active boost is whatever the plan reports now.
                            targetBoost = _cpuPowerService.CurrentMode;
                        }
                    }

                    if (_pendingAcFanRestore || _isDcEcoActive)
                    {
                        try
                        {
                            fanOk = await _agentClient.SetFanModeAsync(targetFan);
                        }
                        catch { fanOk = false; }
                        SetPendingRestore(fan: !fanOk);
                        if (!fanOk) failed.Add("Fan Modu");
                    }

                    if (!_pendingAcModeRestore && !_pendingAcCpuRestore && !_pendingAcFanRestore)
                    {
                        ClearEcoBaseline();
                    }
                    else
                    {
                        allRestored = false;
                    }
                    PersistEcoState();
                }

                // Only undo what Eco did: restore the panel rate it lowered. If Eco never changed it, the display
                // is left alone (it used to be forced to its maximum on every AC event, including startup).
                int? restoredRate = RestorePanelRefreshRateAfterEco();
                if (HasPendingPanelRestore)
                {
                    allRestored = false;
                    failed.Add(Loc.T("Ekran yenileme hızı"));
                }

                string message = !restoreAttempted && !restoredRate.HasValue
                    ? Loc.T("Adaptör Bağlandı: Değişiklik gerekmedi.")
                    : allRestored
                        ? (restoredRate.HasValue
                            ? Loc.T($"Adaptör Bağlandı: Önceki profil ve {restoredRate}Hz ekran tazeleme hızı geri yüklendi.")
                            : Loc.T("Adaptör Bağlandı: Önceki profil geri yüklendi."))
                        : Loc.T($"Adaptör Bağlandı (Kısmi Geri Yükleme): Geri yüklenemeyen ayarlar: {string.Join(", ", failed)}");

                PowerModeSwitched?.Invoke(new PowerTransitionEventArgs
                {
                    IsAc = true,
                    Message = message,
                    ModeApplied = modeOk,
                    VerifiedMode = modeOk ? targetMode : (GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default),
                    BoostApplied = boostOk,
                    VerifiedBoost = boostOk ? targetBoost : _cpuPowerService.CurrentMode,
                    FanApplied = fanOk,
                    VerifiedFan = fanOk ? targetFan : (GetCurrentFanMode?.Invoke() ?? FanMode.Auto)
                });
            }
        }
        finally
        {
            _transitionLock?.Release();
        }
    }

    public async Task<bool> RetryPendingAcRestoreAsync(bool acquireLock = true)
    {
        if (!HasPendingAcRestore || _isDisposed) return true;
        if (!_lastAcStatus) return false; // Can only retry AC restore when connected to AC

        if (acquireLock && _transitionLock != null)
        {
            await _transitionLock.WaitAsync();
        }
        try
        {
            if (!HasPendingAcRestore || _isDisposed || !_lastAcStatus) return !HasPendingAcRestore;

            bool isGame = IsGameSessionActive?.Invoke() == true;
            var activeGame = GetActiveGameProfile?.Invoke();

            OperatingMode targetMode = (isGame && activeGame != null) ? activeGame.TargetMode : _savedOperatingModeBeforeDc;
            CpuBoostMode targetBoost = (isGame && activeGame != null) ? activeGame.TargetCpuBoost : _savedCpuBoostBeforeDc;
            FanMode targetFan = (isGame && activeGame != null) ? activeGame.TargetFanMode : _savedFanModeBeforeDc;

            bool modeAppliedThisRun = false;
            bool boostAppliedThisRun = false;
            bool fanAppliedThisRun = false;

            if (_pendingAcModeRestore)
            {
                bool ok = false;
                try { ok = await _agentClient.SetOperatingModeAsync(targetMode); } catch { ok = false; }
                if (ok)
                {
                    SetPendingRestore(mode: false);
                    modeAppliedThisRun = true;
                }
            }

            if (_pendingAcCpuRestore)
            {
                bool ok = false;
                bool gameTarget = isGame && activeGame != null;
                try { ok = await RestoreBoostAfterEcoAsync(gameTarget, targetBoost); } catch { ok = false; }
                if (ok)
                {
                    SetPendingRestore(cpu: false);
                    boostAppliedThisRun = true;
                    if (!gameTarget) targetBoost = _cpuPowerService.CurrentMode;
                }
            }

            if (_pendingAcFanRestore)
            {
                bool ok = false;
                try { ok = await _agentClient.SetFanModeAsync(targetFan); } catch { ok = false; }
                if (ok)
                {
                    SetPendingRestore(fan: false);
                    fanAppliedThisRun = true;
                }
            }

            if (!HasPendingAcRestore)
            {
                ClearEcoBaseline();
            }
            PersistEcoState();

            // Fire event whenever at least one command was actually sent and succeeded in this retry run,
            // even if another step remains pending (partial success). Only mark *Applied = true for
            // commands that were genuinely executed and verified in this run.
            if (modeAppliedThisRun || boostAppliedThisRun || fanAppliedThisRun)
            {
                string message = !HasPendingAcRestore
                    ? Loc.T("Adaptör ayarları başarıyla yeniden uygulandı.")
                    : Loc.T("Adaptör ayarları kısmen uygulandı.");

                PowerModeSwitched?.Invoke(new PowerTransitionEventArgs
                {
                    IsAc = true,
                    Message = message,
                    ModeApplied = modeAppliedThisRun,
                    VerifiedMode = modeAppliedThisRun ? targetMode : (GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default),
                    BoostApplied = boostAppliedThisRun,
                    VerifiedBoost = boostAppliedThisRun ? targetBoost : _cpuPowerService.CurrentMode,
                    FanApplied = fanAppliedThisRun,
                    VerifiedFan = fanAppliedThisRun ? targetFan : (GetCurrentFanMode?.Invoke() ?? FanMode.Auto)
                });
            }

            return !HasPendingAcRestore;
        }
        finally
        {
            if (acquireLock)
            {
                _transitionLock?.Release();
            }
        }
    }

    /// <summary>
    /// Puts the built-in panel back to the refresh rate Eco replaced with 60 Hz. Returns the restored rate, or null
    /// when Eco had not changed it (nothing is touched) or the panel is not available right now (kept for next time).
    /// </summary>
    private int? RestorePanelRefreshRateAfterEco()
    {
        var saved = _savedRefreshRateBeforeEco;
        if (!saved.HasValue) return null;
        if (!_displayService.SetRefreshRate(saved.Value)) return null;
        _savedRefreshRateBeforeEco = null;
        PersistEcoState();
        return saved;
    }

    /// <summary>
    /// Undo Eco's CPU boost change. A running game's profile applies to both AC and DC values;
    /// otherwise only the battery (DC) value is put back, because Eco never modified the AC value.
    /// </summary>
    private Task<bool> RestoreBoostAfterEcoAsync(bool applyGameProfile, CpuBoostMode targetBoost)
    {
        if (applyGameProfile)
        {
            return _cpuPowerService.SetBoostModeAsync(targetBoost);
        }
        // Never fall back to the AC baseline: writing it into the DC slot would give the battery a value the user
        // never had there. An unknown DC value means "leave the battery value alone".
        if (!_savedDcBoostBeforeEco.HasValue) return Task.FromResult(true);
        return _cpuPowerService.SetBoostModeAsync(_savedDcBoostBeforeEco.Value, targetAc: false, targetDc: true);
    }

    /// <summary>
    /// Puts the desktop baseline back when the app exits while Eco is active, so nothing is left behind.
    /// The display refresh rate is only raised when automatic switching is enabled.
    /// </summary>
    public async Task<bool> RevertEcoForExitAsync()
    {
        if (_isDisposed || (!_isDcEcoActive && !HasPendingAcRestore)) return true;

        if (_transitionLock != null) await _transitionLock.WaitAsync();
        try
        {
            bool modeOk = false, boostOk = false, fanOk = false;
            try { modeOk = await _agentClient.SetOperatingModeAsync(_savedOperatingModeBeforeDc); }
            catch (Exception ex) { AppLog.Warn("Çıkışta çalışma modu geri alınamadı", ex.Message); }
            try { boostOk = await RestoreBoostAfterEcoAsync(false, _savedCpuBoostBeforeDc); }
            catch (Exception ex) { AppLog.Warn("Çıkışta CPU boost geri alınamadı", ex.Message); }
            try { fanOk = await _agentClient.SetFanModeAsync(_savedFanModeBeforeDc); }
            catch (Exception ex) { AppLog.Warn("Çıkışta fan modu geri alınamadı", ex.Message); }

            RestorePanelRefreshRateAfterEco();

            bool ok = modeOk && boostOk && fanOk;
            if (ok)
            {
                SetPendingRestore(mode: false, cpu: false, fan: false);
                ClearEcoBaseline();
            }
            PersistEcoState();
            return ok;
        }
        finally
        {
            _transitionLock?.Release();
        }
    }

    private static bool QueryHardwareAcStatus()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                return status.ACLineStatus == 1 || status.ACLineStatus == 255;
            }
        }
        catch { }
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }

        _debounceTimer.Stop();
        _debounceTimer.Dispose();
    }
}
