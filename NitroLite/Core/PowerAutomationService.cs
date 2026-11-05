using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Win32;

namespace NitroLite.Core;

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

    private OperatingMode _savedOperatingModeBeforeDc = OperatingMode.Default;
    private CpuBoostMode _savedCpuBoostBeforeDc = CpuBoostMode.Aggressive;
    private FanMode _savedFanModeBeforeDc = FanMode.Auto;

    public bool IsDcEcoActive => _isDcEcoActive;
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
    public Func<OperatingMode>? GetCurrentOperatingMode { get; set; }
    public Func<FanMode>? GetCurrentFanMode { get; set; }

    public event Action<bool, string>? PowerModeSwitched; // bool isAc, string statusMessage

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

        if (isAc == _lastAcStatus && _hasEvaluatedInitialState)
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
            if (isAc == _lastAcStatus && _hasEvaluatedInitialState)
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
                    _savedOperatingModeBeforeDc = GetCurrentOperatingMode?.Invoke() ?? OperatingMode.Default;
                    _savedCpuBoostBeforeDc = _cpuPowerService.CurrentMode;
                    _savedFanModeBeforeDc = GetCurrentFanMode?.Invoke() ?? FanMode.Auto;
                    _isDcEcoActive = true;

                    try
                    {
                        await _agentClient.SetOperatingModeAsync(OperatingMode.Quiet);
                    }
                    catch { }

                    try
                    {
                        await _cpuPowerService.SetBoostModeAsync(CpuBoostMode.Disabled);
                    }
                    catch { }

                    try
                    {
                        await _agentClient.SetFanModeAsync(FanMode.Auto);
                    }
                    catch { }

                    if (_settings.AutoRefreshRateSwitching)
                    {
                        _displayService.SetRefreshRate(60);
                    }

                    PowerModeSwitched?.Invoke(false, "Pilde Çalışma (Eco Modu): 60Hz, Sessiz Fan ve Güç Tasarrufu uygulandı.");
                }
                else
                {
                    // Balanced mode: retain active settings (NitroSense behavior)
                    _isDcEcoActive = false;
                    PowerModeSwitched?.Invoke(false, "Pilde Çalışma (Dengeli Mod): Mevcut donanım ayarları korundu.");
                }
            }
            else
            {
                // Transition to AC (Adapter Connected)
                if (_isDcEcoActive)
                {
                    try
                    {
                        await _agentClient.SetOperatingModeAsync(_savedOperatingModeBeforeDc);
                    }
                    catch { }

                    try
                    {
                        await _cpuPowerService.SetBoostModeAsync(_savedCpuBoostBeforeDc);
                    }
                    catch { }

                    try
                    {
                        await _agentClient.SetFanModeAsync(_savedFanModeBeforeDc);
                    }
                    catch { }

                    _isDcEcoActive = false;
                }

                int targetRate = 165;
                if (_settings.AutoRefreshRateSwitching)
                {
                    int maxRate = _displayService.GetMaxRefreshRate();
                    targetRate = maxRate > 60 ? maxRate : 165;
                    _displayService.SetRefreshRate(targetRate);
                }

                PowerModeSwitched?.Invoke(true, $"Adaptör Bağlandı: Önceki profil ve {targetRate}Hz tazeleme hızı geri yüklendi.");
            }
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
        GC.SuppressFinalize(this);
    }
}
