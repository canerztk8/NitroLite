using System;
using System.Runtime.InteropServices;
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
    private bool _lastAcStatus = true;
    private bool _hasEvaluatedInitialState = false;
    private bool _isDcEcoActive = false;
    private bool _isDisposed = false;

    private bool _pendingAcModeRestore = false;
    private bool _pendingAcCpuRestore = false;
    private bool _pendingAcFanRestore = false;

    private OperatingMode _savedOperatingModeBeforeDc = OperatingMode.Default;
    private CpuBoostMode _savedCpuBoostBeforeDc = CpuBoostMode.Aggressive;
    private FanMode _savedFanModeBeforeDc = FanMode.Auto;

    public bool IsDcEcoActive => _isDcEcoActive;
    public bool HasPendingAcRestore => _pendingAcModeRestore || _pendingAcCpuRestore || _pendingAcFanRestore;
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
        if (!force && _isDcEcoActive)
        {
            // Do not corrupt previously saved AC desktop baseline when on DC Eco mode!
            return;
        }
        _savedOperatingModeBeforeDc = mode;
        _savedCpuBoostBeforeDc = boost;
        _savedFanModeBeforeDc = fan;
    }

    public void SetDcEcoActive(bool active)
    {
        _isDcEcoActive = active;
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
        await EvaluatePowerStateAsync();
    }

    public async Task EvaluatePowerStateAsync(bool? forceAcStatus = null)
    {
        bool isAc = forceAcStatus ?? QueryHardwareAcStatus();
        await ProcessPowerTransitionAsync(isAc);
    }

    public async Task ProcessPowerTransitionAsync(bool isAc)
    {
        if (_isDisposed) return;

        if (isAc == _lastAcStatus && _hasEvaluatedInitialState && !HasPendingAcRestore)
        {
            return;
        }

        if (_transitionLock != null)
        {
            await _transitionLock.WaitAsync();
        }
        try
        {
            if (_isDisposed) return;
            if (isAc == _lastAcStatus && _hasEvaluatedInitialState && !HasPendingAcRestore)
            {
                return;
            }

            _lastAcStatus = isAc;
            _hasEvaluatedInitialState = true;

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
                    }
                    _isDcEcoActive = true;

                    bool modeOk = false;
                    try
                    {
                        modeOk = await _agentClient.SetOperatingModeAsync(OperatingMode.Quiet);
                    }
                    catch { modeOk = false; }

                    bool boostOk = false;
                    try
                    {
                        boostOk = await _cpuPowerService.SetBoostModeAsync(CpuBoostMode.Disabled);
                    }
                    catch { boostOk = false; }

                    bool fanOk = false;
                    try
                    {
                        fanOk = await _agentClient.SetFanModeAsync(FanMode.Auto);
                    }
                    catch { fanOk = false; }

                    if (_settings.AutoRefreshRateSwitching)
                    {
                        _displayService.SetRefreshRate(60);
                    }

                    var failed = new System.Collections.Generic.List<string>();
                    if (!modeOk) failed.Add("Sessiz Mod");
                    if (!boostOk) failed.Add("CPU Boost (Devre Dışı)");
                    if (!fanOk) failed.Add("Fan Modu (Auto)");

                    string message = failed.Count == 0
                        ? "Pilde Çalışma (Eco Modu): 60Hz, Sessiz Fan ve Güç Tasarrufu uygulandı."
                        : $"Pilde Çalışma (Kısmi Eco): Uygulanamayan ayarlar: {string.Join(", ", failed)}";

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
                    _isDcEcoActive = false;
                    PowerModeSwitched?.Invoke(new PowerTransitionEventArgs
                    {
                        IsAc = false,
                        Message = "Pilde Çalışma (Dengeli Mod): Mevcut donanım ayarları korundu.",
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
                var failed = new System.Collections.Generic.List<string>();

                bool modeOk = false;
                bool boostOk = false;
                bool fanOk = false;
                OperatingMode targetMode = _savedOperatingModeBeforeDc;
                CpuBoostMode targetBoost = _savedCpuBoostBeforeDc;
                FanMode targetFan = _savedFanModeBeforeDc;

                if (_isDcEcoActive || HasPendingAcRestore)
                {
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
                        _pendingAcModeRestore = !modeOk;
                        if (!modeOk) failed.Add("Çalışma Modu");
                    }

                    if (_pendingAcCpuRestore || _isDcEcoActive)
                    {
                        try
                        {
                            boostOk = await _cpuPowerService.SetBoostModeAsync(targetBoost);
                        }
                        catch { boostOk = false; }
                        _pendingAcCpuRestore = !boostOk;
                        if (!boostOk) failed.Add("CPU Boost");
                    }

                    if (_pendingAcFanRestore || _isDcEcoActive)
                    {
                        try
                        {
                            fanOk = await _agentClient.SetFanModeAsync(targetFan);
                        }
                        catch { fanOk = false; }
                        _pendingAcFanRestore = !fanOk;
                        if (!fanOk) failed.Add("Fan Modu");
                    }

                    if (!_pendingAcModeRestore && !_pendingAcCpuRestore && !_pendingAcFanRestore)
                    {
                        _isDcEcoActive = false;
                    }
                    else
                    {
                        allRestored = false;
                    }
                }

                int targetRate = 165;
                if (_settings.AutoRefreshRateSwitching)
                {
                    int maxRate = _displayService.GetMaxRefreshRate();
                    targetRate = maxRate > 60 ? maxRate : 165;
                    _displayService.SetRefreshRate(targetRate);
                }

                string message = allRestored
                    ? $"Adaptör Bağlandı: Önceki profil ve {targetRate}Hz tazeleme hızı geri yüklendi."
                    : $"Adaptör Bağlandı (Kısmi Geri Yükleme): Geri yüklenemeyen ayarlar: {string.Join(", ", failed)}";

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
                    _pendingAcModeRestore = false;
                    modeAppliedThisRun = true;
                }
            }

            if (_pendingAcCpuRestore)
            {
                bool ok = false;
                try { ok = await _cpuPowerService.SetBoostModeAsync(targetBoost); } catch { ok = false; }
                if (ok)
                {
                    _pendingAcCpuRestore = false;
                    boostAppliedThisRun = true;
                }
            }

            if (_pendingAcFanRestore)
            {
                bool ok = false;
                try { ok = await _agentClient.SetFanModeAsync(targetFan); } catch { ok = false; }
                if (ok)
                {
                    _pendingAcFanRestore = false;
                    fanAppliedThisRun = true;
                }
            }

            if (!HasPendingAcRestore)
            {
                _isDcEcoActive = false;
            }

            // Fire event whenever at least one command was actually sent and succeeded in this retry run,
            // even if another step remains pending (partial success). Only mark *Applied = true for
            // commands that were genuinely executed and verified in this run.
            if (modeAppliedThisRun || boostAppliedThisRun || fanAppliedThisRun)
            {
                string message = !HasPendingAcRestore
                    ? "Adaptör ayarları başarıyla yeniden uygulandı."
                    : "Adaptör ayarları kısmen uygulandı.";

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
        GC.SuppressFinalize(this);
    }
}
