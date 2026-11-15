using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfLine = System.Windows.Shapes.Line;
using WpfPoint = System.Windows.Point;

namespace NitroLite;

public partial class MainWindow : Window
{
    private AcerAgentClient _agentClient;
    private BatteryWatchdogService _batteryService;
    private readonly DebloatService _debloatService;
    private CpuPowerService _cpuPowerService;
    private GameDetectionService _gameService;
    private readonly RgbProfileService _rgbProfileService;
    private readonly HardwareMonitorService _hardwareService;
    private readonly MemoryCleanerService _memoryService;
    private readonly StorageHealthService _storageService;
    private readonly IDisplayRefreshRateService _displayService;
    private readonly IScreenBrightnessService _screenBrightnessService;
    private AtkMouseService _atkMouseService;
    private PowerAutomationService? _powerAutomationService;
    private SchoolModeService? _schoolModeService;
    private readonly BatteryWearHistory _wearHistory;
    // The vermilion dot on the newest wear reading, created once and moved on every render.
    private System.Windows.Shapes.Ellipse? _wearTrendDot;

    private readonly DispatcherTimer _pollTimer = new();
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _menuItemRetryDesktop;
    private OsdOverlayWindow? _osdWindow;
    private bool _isOsdOpen = false;
    private readonly List<MonitorData> _telemetryHistory = new();
    // Mode changes drawn as vermilion ticks on the recording strip, plus the pooled Line elements
    // that draw them: reused every render so the strip never allocates visual children.
    private readonly List<(DateTime At, OperatingMode Mode)> _modeMarks = new();
    private readonly List<System.Windows.Shapes.Line> _traceMarkLines = new();
    private const int MaxTraceMarks = 8;
    private string _selectedChartMetric = "CPU_TEMP";
    private DispatcherTimer? _toastTimer;
    private long _telemetrySampleIndex = 0;

    private OperatingMode _currentMode = OperatingMode.Default;
    private OperatingMode _savedModeBeforeGame = OperatingMode.Default;
    private CpuBoostMode _savedCpuBoostBeforeGame = CpuBoostMode.Aggressive;
    private FanMode _currentFanModeValue = FanMode.Auto;

    // Every assignment (manual, game profile, power transition, device sync) also refreshes the fan buttons.
    private FanMode _currentFanMode
    {
        get => _currentFanModeValue;
        set
        {
            _currentFanModeValue = value;
            if (Dispatcher.CheckAccess()) UpdateFanButtonsUi(value);
            else Dispatcher.BeginInvoke(new Action(() => UpdateFanButtonsUi(value)));
        }
    }
    private FanMode _savedFanModeBeforeGame = FanMode.Auto;
    private int _savedMouseProfileBeforeGame = 1;
    private bool _isMouseProfileSwitchedForGame = false;
    // The last Config the sidebar row showed, so a placeholder call cannot erase it.
    private int _lastShownMouseProfile = 0;
    private GpuOperatingMode? _currentGpuMode = null;
    private bool _isGameSessionActive = false;
    private bool _hasPendingDesktopRestore = false;
    private bool _hasGameBaselineCaptured = false; // Separate from pending restore: true only after an actual game baseline was saved
    private bool _pendingModeRestore = false;
    private bool _pendingCpuBoostRestore = false;
    private bool _pendingFanRestore = false;
    private bool _pendingMouseRestore = false;
    private int _gameSessionGeneration = 0;
    private readonly SemaphoreSlim _gameProfileTransitionLock = new(1, 1);

    private GameSessionRecoveryState? _interruptedGameRecovery;
    private GameSessionRecoveryState? _activeGameRecovery;
    private long _nextGameRecoveryAttempt;
    private long _nextPanelRestoreAttempt;
    public bool HasPendingDesktopRestore => _interruptedGameRecovery != null || _hasPendingDesktopRestore || _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore || _pendingMouseRestore || (_powerAutomationService?.HasPendingAcRestore == true);
    internal bool PendingModeRestore => _pendingModeRestore;
    internal bool PendingCpuBoostRestore => _pendingCpuBoostRestore;
    internal bool PendingFanRestore => _pendingFanRestore;
    internal bool PendingMouseRestore => _pendingMouseRestore;
    internal OperatingMode SavedModeBeforeGame => _savedModeBeforeGame;
    internal CpuBoostMode SavedCpuBoostBeforeGame => _savedCpuBoostBeforeGame;
    internal int SavedMouseProfileBeforeGame => _savedMouseProfileBeforeGame;
    internal bool HasGameBaselineCaptured => _hasGameBaselineCaptured;
    internal FanMode CurrentFanMode => _currentFanMode;
    internal OperatingMode CurrentMode => _currentMode;
    internal AppSettings CurrentAppSettings => _appSettings;
    internal void SetCurrentModeForTesting(OperatingMode m) => _currentMode = m;
    internal void SetLightingForTesting(LightingConfig cfg) => _lighting = cfg;
    internal void ReportServiceAvailabilityForTesting(bool available) => UpdateServiceTelemetryState(available);
    internal bool ServiceBannerShownForTesting => ServiceBanner?.Visibility == Visibility.Visible;
    internal int TelemetryFailureCountForTesting => _consecutiveTelemetryFailures;
    internal bool? LastServiceTelemetryAvailableForTesting => _lastServiceTelemetryAvailable;
    internal bool IsGameSessionActive => _isGameSessionActive;
    internal SemaphoreSlim GameProfileTransitionLock => _gameProfileTransitionLock;
    internal Func<Func<Task>, Task>? DispatcherRunner { get; set; }
    internal Func<int, bool>? MouseProfileSwitcher { get; set; }

    /// <summary>
    /// Tick count of the last user input. Production reads the real one; tests drive it so the
    /// input-triggered mouse retry can be exercised without injecting system-wide input.
    /// </summary>
    internal Func<long> UserActivityProvider { get; set; } = () => UserActivity.LastInputTick;
    internal Action<string, bool>? ToastHandler { get; set; }
    internal PowerAutomationService? PowerAutomation => _powerAutomationService;

    internal void SetPendingRestoreForTesting(
        bool hasPending,
        bool pendingMode = false,
        bool pendingCpu = false,
        bool pendingFan = false,
        bool pendingMouse = false,
        OperatingMode savedMode = OperatingMode.Default,
        CpuBoostMode savedCpu = CpuBoostMode.Aggressive,
        FanMode savedFan = FanMode.Auto,
        int savedMouse = 1,
        bool? hasGameBaselineCaptured = null)
    {
        _hasPendingDesktopRestore = hasPending;
        _hasGameBaselineCaptured = hasGameBaselineCaptured ?? hasPending;
        _pendingModeRestore = pendingMode;
        _pendingCpuBoostRestore = pendingCpu;
        _pendingFanRestore = pendingFan;
        _pendingMouseRestore = pendingMouse;
        _savedModeBeforeGame = savedMode;
        _savedCpuBoostBeforeGame = savedCpu;
        _savedFanModeBeforeGame = savedFan;
        _savedMouseProfileBeforeGame = savedMouse;
        UpdateTrayRetryMenuItemState();
    }

    private LightingConfig _lighting = new();
    private bool _isApplyingPreset = false;
    private bool _isRebuildingEffectList = false;
    private bool _isUpdatingGameUi = false;
    private bool _isInitializingUi = true;
    private bool _isPolling = false;
    private bool _isExiting = false;

    private static readonly string ActiveRgbConfigFile = AppPaths.GetDataFile("active_rgb_state.json");
    private static readonly string AppSettingsConfigFile = AppPaths.GetDataFile("settings.json");
    private static readonly string LegacyConfigFile = AppPaths.GetDataFile("config.json");
    private static readonly string GameSessionRecoveryFile = AppPaths.GetDataFile("active_game_session.json");
    private static readonly string DcEcoStateFile = AppPaths.GetDataFile("dc_eco_state.json");
    private static readonly string GameProfilesFile = AppPaths.GetDataFile("game_profiles.json");
    private static readonly string RgbPresetsFile = AppPaths.GetDataFile("rgb_presets.json");
    private AppSettings _appSettings = new();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const int SW_SHOWMAXIMIZED = 3;

    private Task? _initTask;
    private readonly object _initLock = new();
    private bool _initFailed;

    /// <summary>
    /// Runs the first refresh exactly once and hands every caller the same task. The old version set a flag before
    /// the work started, so the second caller (the constructor starts this fire-and-forget and the Loaded handler
    /// calls it again) returned immediately and then acted on state that had not been read yet. A failed run also
    /// left the flag set, so it never retried.
    /// </summary>
    public Task EnsureInitializedAsync()
    {
        lock (_initLock)
        {
            if (_initTask == null || _initFailed)
            {
                _initFailed = false;
                _initTask = RunInitializationAsync();
            }
            return _initTask;
        }
    }

    private async Task RunInitializationAsync()
    {
        try
        {
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            // A service that was not up yet must not leave the window permanently empty: the next call retries.
            AppLog.Error("Başlangıç yenilemesi başarısız", ex.Message);
            _initFailed = true;
        }
    }

    public MainWindow(bool startMinimized = false)
    {
        _agentClient = new();
        _batteryService = new();
        _debloatService = new();
        _cpuPowerService = new();
        _gameService = new();
        _rgbProfileService = new();
        _hardwareService = new();
        _memoryService = new();
        _storageService = new();
        _wearHistory = new BatteryWearHistory();
        _displayService = new DisplayRefreshRateService();
        _screenBrightnessService = new ScreenBrightnessService();
        _atkMouseService = new();

        InitializeComponent();
        LocXaml.Apply(this);
        WindowChromeHelper.Attach(this);
        ShowAppVersion();
        SetupTrayIcon();
        SetupTimers();
        SetupEvents();

        if (startMinimized)
        {
            WindowState = WindowState.Minimized;
            Hide();
        }

        // Guarantees all services, settings, and staged boot backlight pulses
        // execute immediately even when launching hidden/minimized to system tray on boot!
        _ = EnsureInitializedAsync();
    }

    internal MainWindow(
        AcerAgentClient agentClient,
        CpuPowerService cpuPowerService,
        GameDetectionService gameService,
        AtkMouseService atkMouseService,
        BatteryWatchdogService? batteryService = null,
        AppSettings? settings = null,
        Func<int, bool>? mouseProfileSwitcher = null,
        bool isTestHarness = true,
        IDisplayRefreshRateService? displayService = null,
        HardwareMonitorService? hardwareService = null,
        IScreenBrightnessService? screenBrightnessService = null,
        BatteryWearHistory? wearHistory = null)
    {
        _agentClient = agentClient;
        _cpuPowerService = cpuPowerService;
        _gameService = gameService;
        _atkMouseService = atkMouseService;
        _batteryService = batteryService ?? (isTestHarness ? new BatteryWatchdogService(attachSystemEvents: false) : new BatteryWatchdogService());
        _debloatService = new DebloatService();
        _rgbProfileService = new RgbProfileService();
        _hardwareService = hardwareService ?? (isTestHarness ? new HardwareMonitorService(initNvml: false) : new HardwareMonitorService());
        _memoryService = new MemoryCleanerService();
        _storageService = new StorageHealthService();
        _wearHistory = wearHistory ?? new BatteryWearHistory();
        _displayService = displayService ?? (isTestHarness ? new TestDisplayRefreshRateServiceStub() : new DisplayRefreshRateService());
        _screenBrightnessService = screenBrightnessService ?? (isTestHarness ? new TestScreenBrightnessService() : new ScreenBrightnessService());
        if (settings != null) { _appSettings = settings; _settingsLoaded = true; }
        if (mouseProfileSwitcher != null) MouseProfileSwitcher = mouseProfileSwitcher;

        if (!isTestHarness)
        {
            InitializeComponent();
            LocXaml.Apply(this);
            WindowChromeHelper.Attach(this);
            SetupTrayIcon();
            SetupTimers();
            SetupEvents();
            _ = EnsureInitializedAsync();
        }
        else
        {
            _isInitializingUi = false;
            DispatcherRunner = async action => await action();
            _gameService.GameStarted += OnGameStarted;
            _gameService.GameEnded += OnGameEnded;
            _powerAutomationService = new PowerAutomationService(_agentClient, _cpuPowerService, _displayService, _appSettings, attachSystemEvents: false, transitionLock: _gameProfileTransitionLock);
            _powerAutomationService.GetCurrentOperatingMode = () => _isGameSessionActive ? _savedModeBeforeGame : _currentMode;
            _powerAutomationService.GetCurrentCpuBoost = () => _isGameSessionActive ? _savedCpuBoostBeforeGame : _cpuPowerService.CurrentMode;
            _powerAutomationService.GetCurrentFanMode = () => _isGameSessionActive ? _savedFanModeBeforeGame : _currentFanMode;
            _powerAutomationService.IsGameSessionActive = () => _isGameSessionActive;
            _powerAutomationService.GetActiveGameProfile = () => _gameService.ActiveGameProfile;
            _powerAutomationService.PowerModeSwitched += OnPowerModeSwitchedHandler;
            _schoolModeService = CreateSchoolModeService();
        }
    }

    /// <summary>Shows the version from the project file (Version in NitroLite.csproj) instead of a hard-coded label.</summary>
    private void ShowAppVersion()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (TxtAppVersion != null && v != null) TxtAppVersion.Text = v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
    }

    private void SetupTrayIcon()
    {
        System.Drawing.Icon? appIcon = null;
        try
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                appIcon = new System.Drawing.Icon(iconPath);
            }
            else if (Environment.ProcessPath is { } exe)
            {
                // Single-file release package: app.ico is inside the exe; use the exe's own icon (ApplicationIcon).
                appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            }
        }
        catch { }

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = appIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = Loc.T("NitroLite")
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Göster / Aç"), null, (s, e) => ShowWindow());

        _menuItemRetryDesktop = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Masaüstü ayarlarını yeniden dene"), null, async (s, e) =>
        {
            await RetryPendingDesktopRestoreAsync();
        });
        _menuItemRetryDesktop.Enabled = false;
        menu.Items.Add(_menuItemRetryDesktop);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // Each item calls the same code as the mode cards, so the battery rule for Turbo/Performance applies here too.
        // Tags let the Opening handler tick the active mode and boost.
        void AddMode(string text, OperatingMode mode) =>
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(text, null, async (s, e) => await SetManualModeAsync(mode)) { Tag = mode });
        void AddBoost(string text, CpuBoostMode boost) =>
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(text, null, async (s, e) => await SetManualCpuBoostAsync(boost)) { Tag = boost });

        AddMode(Loc.T("Sessiz Mod"), OperatingMode.Quiet);
        AddMode(Loc.T("Dengeli Mod"), OperatingMode.Default);
        AddMode(Loc.T("Performans Modu"), OperatingMode.Extreme);
        AddMode(Loc.T("Turbo Mod"), OperatingMode.Turbo);
        AddMode(Loc.T("Eco Mod"), OperatingMode.Eco);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddBoost(Loc.T("CPU Boost: Devre Dışı"), CpuBoostMode.Disabled);
        AddBoost(Loc.T("CPU Boost: Verimli"), CpuBoostMode.Efficient);
        AddBoost(Loc.T("CPU Boost: Agresif"), CpuBoostMode.Aggressive);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Çıkış"), null, (s, e) => RealExit());

        menu.Opening += (s, e) =>
        {
            UpdateTrayRetryMenuItemState();
            foreach (var item in menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>())
            {
                item.Checked = item.Tag switch
                {
                    OperatingMode m => m == _currentMode,
                    CpuBoostMode b => _lastShownBoost == b,
                    _ => false
                };
            }
        };

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (s, e) => ShowWindow();
    }

    private void UpdateTrayRetryMenuItemState()
    {
        if (_menuItemRetryDesktop != null)
        {
            _menuItemRetryDesktop.Enabled = HasPendingDesktopRestore && !_isGameSessionActive;
        }
    }

    private void SetupTimers()
    {
        _pollTimer.Interval = TimeSpan.FromSeconds(1);
        _pollTimer.Tick += async (s, e) => await PollTelemetryAsync();
        _pollTimer.Start();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += OnToastTimerTick;
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        if (ToastBanner != null)
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
            ToastBanner.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    private void SetupEvents()
    {
        _batteryService.StatusUpdated += OnBatteryStatusUpdated;
        _batteryService.OverchargeAlert += OnOverchargeAlert;
        _batteryService.DischargeTargetReached += OnDischargeTargetReached;
        _batteryService.DischargeStateChanged += OnDischargeStateChanged;
        _batteryService.Start();

        // Game Detection Hooks
        _gameService.GameStarted += OnGameStarted;
        _gameService.GameEnded += OnGameEnded;

        // Power & Session Switch Event Hooks for Keyboard Backlight Persistence
        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;

        // Unsolicited AASSvc events (mode-switch key, keyboard backlight timeout)
        _agentClient.ServiceEventReceived += OnAgentServiceEvent;

        // AC/DC Power & Display Automation
        _powerAutomationService = new PowerAutomationService(_agentClient, _cpuPowerService, _displayService, _appSettings, attachSystemEvents: true, transitionLock: _gameProfileTransitionLock);
        _powerAutomationService.EcoStateFilePath = DcEcoStateFile;
        _powerAutomationService.GetCurrentOperatingMode = () => _isGameSessionActive ? _savedModeBeforeGame : _currentMode;
        _powerAutomationService.GetCurrentCpuBoost = () => _isGameSessionActive ? _savedCpuBoostBeforeGame : _cpuPowerService.CurrentMode;
        _powerAutomationService.GetCurrentFanMode = () => _isGameSessionActive ? _savedFanModeBeforeGame : _currentFanMode;
        _powerAutomationService.IsGameSessionActive = () => _isGameSessionActive;
        _powerAutomationService.GetActiveGameProfile = () => _gameService.ActiveGameProfile;
        _powerAutomationService.PowerModeSwitched += OnPowerModeSwitchedHandler;
        _schoolModeService = CreateSchoolModeService();

        Loaded += async (s, e) =>
        {
            // A second pass: templates (ContentPresenter, ComboBoxItem chrome) only exist once the window has been
            // measured, so anything the first pass could not reach is translated here. The pass is idempotent.
            LocXaml.Apply(this);
            await EnsureInitializedAsync();
        };

        StateChanged += (s, e) =>
        {
            UpdateTelemetryPollingRate();
            OnWindowStateChangedForChrome();
        };
        if (CanvasHistoryChart != null)
        {
            // Redraw the history chart immediately when the window is resized instead of on the next poll.
            CanvasHistoryChart.SizeChanged += (s, e) => RenderTelemetryChart();
        }
        if (CanvasModeTrace != null)
        {
            CanvasModeTrace.SizeChanged += (s, e) => RenderModeTrace();
        }
        IsVisibleChanged += (s, e) => UpdateTelemetryPollingRate();
    }

    private void OnPowerModeSwitchedHandler(PowerTransitionEventArgs e)
    {
        Action act = () =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (!_isGameSessionActive)
            {
                if (e.ModeApplied)
                {
                    UpdateModeUi(e.VerifiedMode);
                    _currentMode = e.VerifiedMode;
                }
                if (e.FanApplied)
                {
                    _currentFanMode = e.VerifiedFan;
                }
                if (e.BoostApplied)
                {
                    UpdateBoostUi(e.VerifiedBoost);
                }
            }
            if (!_isInitializingUi && !string.IsNullOrWhiteSpace(e.Message))
            {
                ShowToast(e.Message);
            }
        };

        if (DispatcherRunner != null)
        {
            _ = DispatcherRunner(() => { act(); return Task.CompletedTask; });
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            act();
        }
        else
        {
            Dispatcher.BeginInvoke(act);
        }
    }

    private void UpdateTelemetryPollingRate()
    {
        bool isHiddenOrMinimized = !IsVisible || WindowState == WindowState.Minimized;
        // The OSD is used while gaming with NitroLite in the tray: keep it at 1 s while it is shown.
        bool osdShown = _isOsdOpen && _osdWindow != null && _osdWindow.IsVisible;
        // Otherwise, when minimized in tray, throttle polling to 3000ms to reduce background CPU cycles & power
        var targetInterval = isHiddenOrMinimized && !osdShown ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(1);
        if (_pollTimer.Interval != targetInterval)
        {
            _pollTimer.Interval = targetInterval;
        }
    }

    private async Task RefreshAllAsync()
    {
        _isInitializingUi = true;
        try
        {
            // 0. Load App Settings (Start Minimized, OSD, Window Position, Keyboard Lighting)
            LoadSettings();

            // 0.1 Check for interrupted game session recovery (e.g. unexpected reboot/crash during gaming)
            var recovery = LoadGameSessionRecoveryState();
            if (recovery != null && recovery.IsActive)
            {
                _interruptedGameRecovery = recovery;
                await RetryPendingDesktopRestoreAsync();
            }

            // 0.2 Eco baseline left behind by a crash or exit on battery. Load it before the startup mode is
            // written: RestoreStartupOperatingModeAsync persists the manual mode, which deletes this file and
            // loses the desktop settings the battery Eco pass still has to restore.
            if (_powerAutomationService!.RestorePersistedEcoState())
            {
                AppLog.Warn("Eco temel durumu kurtarıldı", "Önceki çalışmadan kalan pil profili geri yüklenecek.");
            }

            // 1. Initial Mode (Restore saved manual mode or query hardware)
            await RestoreStartupOperatingModeAsync();
            _ = ReassertOperatingModeAsync(isBoot: true);

            // 1.0 Initial fan mode: read it instead of assuming Auto (NitroSense may have left it on Max).
            var initialFan = await _agentClient.GetFanModeAsync();
            if (initialFan.HasValue) _currentFanMode = initialFan.Value;
            UpdateFanButtonsUi(_currentFanMode, initialFan.HasValue);

            // The curve owns the fans when it is on: take them now instead of waiting for the first poll.
            if (_appSettings.FanCurveEnabled) await SetFanCurveEnabledAsync(true, quiet: true);

            // 1.05 Hardware names (CPU, GPU, memory) from Windows, read in the background
            _ = LoadHardwareInfoAsync();

            // 1.1 Initial GPU Mode (MUX Switch)
            await RefreshGpuModeAsync();

            // 1.15 Which operating modes this model actually offers. Read before the mode cards settle so a Nitro
            // without Turbo never shows the button, not even for the first frame.
            await RefreshModeCapabilityAsync();

            // 1.16 What the keyboard can do, so a single-zone deck never shows a four-zone picker.
            await RefreshKeyboardCapabilityAsync();

            // 2. Initial Lighting (Check settings.json, then active_rgb_state.json)
            bool restoredSavedRgb = false;

            if ((_appSettings.KeyboardLighting != null ? IsLightingOff(_appSettings.KeyboardLighting) : _appSettings.IsBacklightOff) /* KeyboardLighting is the source of truth; IsBacklightOff only for old settings files */)
            {
                _lighting.Brightness = 0;
                _lighting.Effect = "OFF";
                _lighting.Color = "#000000";
                _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };
                UpdateLightingUi(_lighting);
                UpdateEffectComboBox("OFF");
                await _agentClient.TurnOffLightingAsync();
                restoredSavedRgb = true;
            }
            else if (_appSettings.KeyboardLighting != null)
            {
                _lighting = _appSettings.KeyboardLighting;
                UpdateLightingUi(_lighting);
                UpdateEffectComboBox(_lighting.Effect);

                if (_lighting.Brightness == 0 || _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    await _agentClient.TurnOffLightingAsync();
                }
                else
                {
                    await _agentClient.SetLightingAsync(_lighting.Effect, _lighting.Brightness, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs.ToArray());
                }
                restoredSavedRgb = true;
            }
            else if (File.Exists(ActiveRgbConfigFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(ActiveRgbConfigFile);
                    var savedRgb = LightingPolicy.Sanitize(JsonSerializer.Deserialize<LightingConfig>(json));
                    if (savedRgb != null)
                    {
                        _lighting = savedRgb;
                        UpdateLightingUi(_lighting);
                        UpdateEffectComboBox(_lighting.Effect);

                        if (_lighting.Brightness == 0 || string.Equals(_lighting.Effect, "OFF", StringComparison.OrdinalIgnoreCase))
                        {
                            await _agentClient.TurnOffLightingAsync();
                        }
                        else
                        {
                            await _agentClient.SetLightingAsync(_lighting.Effect, _lighting.Brightness, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs.ToArray());
                        }
                        restoredSavedRgb = true;
                    }
                }
                catch { }
            }

            if (!restoredSavedRgb)
            {
                var cfg = await _agentClient.GetLightingConfigAsync();
                if (cfg != null)
                {
                    _lighting = cfg;
                    UpdateLightingUi(cfg);
                    SaveSettings();
                    SaveActiveRgbState();
                }
            }

            // 2.0b Ask the agent whether this model has controllable lighting at all. Only an explicit "no"
            // disables the controls; an unanswered query leaves the tab exactly as it was.
            _ = RefreshLightingCapabilityAsync();

            // 2.1 Staged Background Reassertion for Cold Boot / Windows Logon
            _ = ReassertKeyboardLightingAsync(isBoot: true);

            // 2.2 Mirror the device's 30 s idle-off setting in the checkbox
            await SyncBacklightTimeoutFromDeviceAsync();

            // 2.3 Mirror the Care Center 80 % charge limit (background; AcerCCAgent may be absent)
            _ = SyncChargeLimitFromCareCenterAsync();

            // 3. CPU Boost Status
            UpdateBoostUi(_cpuPowerService.CurrentMode);

            // 4. RGB Presets list
            PopulateRgbPresets(_appSettings.SelectedRgbPreset);

            // 5. Configured Games list & Default Mouse Profile
            PopulateConfiguredGames();
            if (_gameService.ProfileLoadError is { } profileLoadError) ShowToast(profileLoadError, false);
            InitializeDefaultMouseProfileUi();

            // Tell the user once when settings or RGB presets had to be recovered from a corrupt file.
            var startupNotices = new List<string>();
            if (_settingsRecoveryNotice is { } recoveryNotice) startupNotices.Add(recoveryNotice);
            if (_rgbProfileService.LoadError is { } rgbLoadError) startupNotices.Add(rgbLoadError);
            if (startupNotices.Count > 0)
            {
                _settingsRecoveryNotice = null;
                ShowToast(string.Join(" ", startupNotices), false);
            }

            // 6. Battery Health Details, Thermal Paste / Fan & Storage Health
            await RefreshBatteryHealthAsync();
            await RefreshStorageHealthUi();

            // Eco baseline was already loaded before the startup mode was written (step 0.2).
            await _powerAutomationService.EvaluatePowerStateAsync();

            // School mode owns the machine while it is on, so it is re-applied after the power pass: the settings it
            // writes are absolute, and the undo record on disk is left untouched.
            if (_schoolModeService != null)
            {
                if (_appSettings.SchoolModeEnabled)
                {
                    await _schoolModeService.ReapplyAsync();
                }
                else if (await _schoolModeService.RecoverOrphanedBackupAsync())
                {
                    // A run died between writing the undo record and saving the setting. The machine was still in
                    // school-mode values with the switch showing off; say so instead of leaving the user to wonder
                    // why the laptop got slow.
                    ShowToast(Loc.T("Yarım kalan okul modu geri alındı; ayarlarınız eski hâline döndü."), false);
                }
            }

            // 7. Services page state. An existing autostart entry is kept working (moved exe, --minimized flag in
            // sync with the setting); nothing is written when autostart is off.
            _debloatService.RepairNitroLiteAutoStart(_appSettings.StartMinimized);
            await RefreshServiceListAsync();
            ChkAutoStart.IsChecked = _debloatService.IsNitroLiteAutoStartEnabled();
        }
        catch (Exception ex)
        {
            // A failure here used to leave the window half-initialised with no explanation: the constructor's
            // fire-and-forget call swallowed it and the Loaded handler turned it into a crash.
            AppLog.Error("Başlangıç yüklenemedi", ex.ToString());
            ShowToast(Loc.T($"Başlangıç ayarları yüklenemedi: {ex.Message}"), false);
        }
        finally
        {
            // Mirrored while the flag is still up: the checkbox handler must not treat the initial value as a click.
            SyncFanStallProtectionUi();
            _isInitializingUi = false;
            // The card reads the settings, so it is filled once they are loaded, not in the constructor.
            SetupSettingsCard();
        }
    }

    private void LoadSettings()
    {
        var loadResult = SettingsManager.LoadSettings(AppSettingsConfigFile, AppSettingsConfigFile + ".bak", LegacyConfigFile);
        var loadedSettings = loadResult.Settings;
        foreach (var property in typeof(AppSettings).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(_appSettings, property.GetValue(loadedSettings));
        _settingsLoaded = true;

        // A corrupt settings file used to fall back to defaults silently: the user lost every preference with no
        // explanation. Remember it here and tell them once the window is up.
        if (loadResult.PrimaryCorrupt)
        {
            _settingsRecoveryNotice = loadResult.Source switch
            {
                SettingsLoadSource.Backup => Loc.T("Ayarlar dosyası bozuktu; yedekten (.bak) geri yüklendi."),
                SettingsLoadSource.Legacy => Loc.T("Ayarlar dosyası bozuktu; eski config.json dosyasından geri yüklendi."),
                _ => Loc.T("Ayarlar dosyası bozuktu ve yedeği de yoktu; varsayılan ayarlarla başlandı.")
            };
        }

        // Immediately sync _lighting with loaded settings so it is never uninitialized
        if (!string.IsNullOrEmpty(_appSettings.SelectedRgbPreset))
        {
            var presets = _rgbProfileService.GetPresets();
            var sel = presets.FirstOrDefault(p => p.Name.Equals(_appSettings.SelectedRgbPreset, StringComparison.OrdinalIgnoreCase));
            if (sel != null)
            {
                if (sel.Brightness == 0 || sel.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    _appSettings.IsBacklightOff = true;
                }
                _lighting.Effect = sel.Effect;
                _lighting.Brightness = sel.Brightness;
                _lighting.Speed = sel.Speed;
                _lighting.Direction = sel.Direction;
                _lighting.Color = sel.Color;
                _lighting.LEDs = new List<string>(sel.LEDs);
            }
        }

        if ((_appSettings.KeyboardLighting != null ? IsLightingOff(_appSettings.KeyboardLighting) : _appSettings.IsBacklightOff) /* KeyboardLighting is the source of truth; IsBacklightOff only for old settings files */)
        {
            _lighting.Brightness = 0;
            _lighting.Effect = "OFF";
            _lighting.Color = "#000000";
            _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };
            _appSettings.IsBacklightOff = true;
        }
        else if (_appSettings.KeyboardLighting != null)
        {
            _lighting = _appSettings.KeyboardLighting;
        }

        ChkStartMinimized.IsChecked = _appSettings.StartMinimized;
        if (ChkAtkMouseIntegration != null)
        {
            ChkAtkMouseIntegration.IsChecked = _appSettings.AtkMouseIntegrationEnabled;
        }
        UpdateAtkMouseIntegrationVisibility(_appSettings.AtkMouseIntegrationEnabled);
        LoadCustomFanSettings();
        LoadFanCurveSettings();
        _ = RefreshRamCleanStateAsync();

        // Persisted toggles: apply to the services directly (the Changed handlers ignore events
        // raised while the window is not loaded / still initializing).
        _gameService.AutoGameModeEnabled = _appSettings.AutoGameModeEnabled;
        if (ChkAutoGameMode != null) ChkAutoGameMode.IsChecked = _appSettings.AutoGameModeEnabled;
        if (TxtGameStatus != null && !_appSettings.AutoGameModeEnabled)
        {
            TxtGameStatus.Text = Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
        }
        _batteryService.WatchdogEnabled = _appSettings.BatteryWatchdogEnabled;
        if (ChkWatchdog != null) ChkWatchdog.IsChecked = _appSettings.BatteryWatchdogEnabled;

        _selectedChartMetric = string.IsNullOrEmpty(_appSettings.SelectedChartMetric) ? "CPU_TEMP" : _appSettings.SelectedChartMetric;
        SetComboBoxByTag(CmbChartMetric, _selectedChartMetric);

        // Restored window size, never below the design minimum or larger than the whole desktop.
        if (_appSettings.WindowWidth is double savedWidth && _appSettings.WindowHeight is double savedHeight)
        {
            Width = Math.Clamp(savedWidth, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
            Height = Math.Clamp(savedHeight, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        }

        if (_appSettings.WindowLeft.HasValue && _appSettings.WindowTop.HasValue)
        {
            double left = _appSettings.WindowLeft.Value;
            double top = _appSettings.WindowTop.Value;

            double minLeft = SystemParameters.VirtualScreenLeft;
            double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100;
            double minTop = SystemParameters.VirtualScreenTop;
            double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

            if (left >= minLeft && left <= maxLeft && top >= minTop && top <= maxTop)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_appSettings.WindowMaximized)
        {
            _restoreMaximized = true;
            // Only maximize now if the window is on screen; when started minimized to the tray,
            // ShowWindow() maximizes it the first time it is opened.
            if (IsVisible && WindowState == WindowState.Normal)
            {
                WindowState = WindowState.Maximized;
            }
        }

        if (_appSettings.OsdEnabled)
        {
            ToggleOsd(true, showToast: false);
        }

        if (CmbDcPowerMode != null)
        {
            SetComboBoxByTag(CmbDcPowerMode, _appSettings.DcPowerMode.ToString());
        }
        if (ChkAutoRefreshRate != null)
        {
            ChkAutoRefreshRate.IsChecked = _appSettings.AutoRefreshRateSwitching;
        }
        UpdateDcRefreshOptionState();
        UpdateSchoolModeUi();
        ApplySchoolModeAutomationState();
    }

    private bool _schoolModeUiSyncing;

    private void SaveWindowPosition()
    {
        if (WindowState == WindowState.Minimized) return;

        // RestoreBounds is the window's normal (un-maximized) rectangle from Windows' window placement, valid in
        // both Normal and Maximized state. ActualWidth/Left are not: right after a restore they still hold the
        // maximized size, which saved the full-screen size as the "normal" size.
        var restore = RestoreBounds;
        if (!restore.IsEmpty && restore.Width > 0 && restore.Height > 0)
        {
            _appSettings.WindowLeft = restore.Left;
            _appSettings.WindowTop = restore.Top;
            _appSettings.WindowWidth = restore.Width;
            _appSettings.WindowHeight = restore.Height;
        }
        _appSettings.WindowMaximized = WindowState == WindowState.Maximized;
        SaveSettings();
    }

    // Settings are written only after they were read from disk (or handed in by the test harness). Before that
    // _appSettings holds defaults: a window that failed in InitializeComponent is still closed by WPF on shutdown,
    // and OnClosing → SaveWindowPosition used to write those defaults over the user's real settings.
    private bool _settingsLoaded;

    private bool SaveSettings()
    {
        if (!_settingsLoaded) return false;
        try
        {
            if (!_isInitializingUi && _lighting != null)
            {
                _appSettings.KeyboardLighting = _lighting;
                _appSettings.IsBacklightOff = _lighting.Brightness == 0 || _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);
                if (CmbRgbPresets?.SelectedItem is string presetName && !string.IsNullOrWhiteSpace(presetName))
                {
                    // The combo shows DisplayName; store the stable Name so the selection survives a language change.
                    _appSettings.SelectedRgbPreset = SelectedRgbPreset()?.Name ?? presetName;
                }
            }

            SettingsManager.SaveSettingsAtomic(AppSettingsConfigFile, _appSettings);
            try
            {
                SettingsManager.SaveSettingsAtomic(LegacyConfigFile, _appSettings);
            }
            catch (Exception ex) { App.WriteLog("Eski ayar kopyası kaydedilemedi", ex.Message); }
            return true;
        }
        catch (Exception ex)
        {
            App.WriteLog("Ayarlar kaydedilemedi", ex.Message);
            ShowToast(Loc.T("Ayarlar diske kaydedilemedi. Değişiklikler bu oturumla sınırlı kalabilir."), false);
            return false;
        }
    }

    private CancellationTokenSource? _lightingReassertCts;

    private CancellationTokenSource? _modeReassertCts;

    public void ShowToast(string message, bool isSuccess = true, bool trayBalloon = true)
    {
        ToastHandler?.Invoke(message, isSuccess);
        if (Dispatcher.HasShutdownStarted) return;

        Action action = () =>
        {
            if (Dispatcher.HasShutdownStarted || ToastBanner == null || TxtToastIcon == null || TxtToastMessage == null) return;

            // In the tray (window hidden or minimized) the banner is invisible: actions started from the tray menu, the
            // mode key or automation would give no feedback, and a refusal ("Turbo not on battery") would look like
            // success. Show it as a Windows notification instead.
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                if (!trayBalloon || _notifyIcon == null || _isExiting || _isInitializingUi || _isGameSessionActive) return;
                try
                {
                    _notifyIcon.ShowBalloonTip(isSuccess ? 2500 : 4000, Loc.T("NitroLite"), message,
                        isSuccess ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
                }
                catch { }
                return;
            }

            TxtToastIcon.Text = isSuccess ? "●" : "!";
            TxtToastIcon.Foreground = TryFindResource(isSuccess ? "AccentGreen" : "AccentRed") as SolidColorBrush ?? (isSuccess ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Firebrick);
            ToastBanner.BorderBrush = TryFindResource(isSuccess ? "AccentGreen" : "AccentRed") as SolidColorBrush ?? (isSuccess ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Firebrick);
            TxtToastMessage.Text = message;

            _toastTimer?.Stop();

            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200));
            ToastBanner.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            _toastTimer?.Start();
        };

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.BeginInvoke(action);
        }
    }

    private HardwareInfo? _hardwareInfo;

    // Monotonic: a clock correction must not make the 5 s device-state poll fire in a burst or stall.
    private readonly System.Diagnostics.Stopwatch _deviceStateClock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _lastDeviceStateSyncAt = TimeSpan.MinValue;
    private static readonly TimeSpan DeviceStateSyncInterval = TimeSpan.FromSeconds(5);
    private CpuBoostMode? _lastShownBoost;

    /// <summary>
    /// Brings the UI (and, outside game sessions, the desktop baseline) in line with the device. Skipped while a
    /// mode/game/power transition holds the lock, so a half-applied transition is never read back as a user change.
    /// </summary>
    internal async Task SyncDeviceStateAsync()
    {
        if (_isInitializingUi || _isExiting) return;
        if (!await _gameProfileTransitionLock.WaitAsync(0)) return;
        try
        {
            var mode = await _agentClient.TryGetOperatingModeAsync();
            if (mode.HasValue && mode.Value != _currentMode)
            {
                // If a boot/resume reassertion is in progress for a saved mode, do not treat the EC's boot default as a user change
                if (_modeReassertCts != null && _appSettings.LastOperatingMode.HasValue && mode.Value != _appSettings.LastOperatingMode.Value)
                {
                    // Ignore transient EC default while reassertion is running
                }
                else
                {
                    UpdateModeUi(mode.Value);
                    if (!_isGameSessionActive) UpdateDesktopBaselineForManualChange(mode: mode.Value);
                }
            }

            var fan = await _agentClient.GetFanModeAsync();
            if (fan.HasValue && fan.Value != _currentFanMode)
            {
                _currentFanMode = fan.Value;
                // While the curve owns the fans, a different mode is not a user choice: the curve takes it back
                // on the next poll, so it must not become the desktop baseline.
                if (!_isGameSessionActive && !_fanCurveEnabled) UpdateDesktopBaselineForManualChange(fan: fan.Value);
            }
            UpdateFanButtonsUi(fan ?? _currentFanMode, fan.HasValue);

            // Off the UI thread: the native read is cheap, but its fallback spawns powercfg.exe and waits for it.
            // Called inline this froze the window for up to four seconds every five seconds on a machine where
            // PowerReadACValueIndex fails, which is the "not responding" the user sees.
            var boost = await Task.Run(() => _cpuPowerService.QueryCurrentBoostMode());
            if (_cpuPowerService.IsModeVerified && boost != _lastShownBoost)
            {
                bool externalChange = _lastShownBoost.HasValue;
                UpdateBoostUi(boost);
                if (externalChange && !_isGameSessionActive) UpdateDesktopBaselineForManualChange(boost: boost);
            }

            UpdateHighPowerAvailabilityUi();
        }
        catch
        {
            // Best effort: the next poll tries again.
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    // The idle look of each segmented button is whatever the XAML declared (a red "Maksimum", an
    // amber "Özel"), captured once so switching back restores it instead of flattening the colour.
    private readonly Dictionary<WpfButton, (WpfBrush Background, WpfBrush Foreground)> _segIdle =
        new Dictionary<WpfButton, (WpfBrush, WpfBrush)>();

    /// <summary>
    /// Marks one option of a segmented control as chosen: the whole button takes the board's signal
    /// colour with dark type, the same treatment the active mode row gets.
    /// </summary>
    private void SetSegActive(WpfButton? button, bool active)
    {
        if (button == null) return;
        if (!_segIdle.ContainsKey(button))
            _segIdle[button] = (button.Background, button.Foreground);

        var ink = TryFindResource("TextPrimary") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
        var deep = TryFindResource("PanelBg") as SolidColorBrush ?? System.Windows.Media.Brushes.LightGray;
        var rule = TryFindResource("BorderDark") as SolidColorBrush ?? System.Windows.Media.Brushes.Gray;
        var idle = _segIdle[button];

        // The chosen option is pressed into the paper, not painted red: a deeper ground, full ink
        // type and a 2px ink rule under it. Vermilion stays reserved for what is live right now.
        button.Background = active ? deep : idle.Background;
        button.Foreground = active ? ink : idle.Foreground;
        button.BorderBrush = active ? ink : rule;
        button.BorderThickness = active ? new Thickness(1, 1, 1, 2) : new Thickness(1);
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    // ---------------------------------------------------------------- custom fan speeds

    private bool _customFanPanelPinned;
    private DispatcherTimer? _customFanApplyTimer;

    // ---------------------------------------------------------------- software fan curve

    /// <summary>
    /// Who armed the curve. The fan card and a running game profile can both want it, and only one of them may
    /// own the fans at a time: the game profile takes over for the length of its session and hands back after.
    /// </summary>
    private enum FanCurveOwner { None, Desktop, Game }

    private FanCurveOwner _fanCurveOwner = FanCurveOwner.None;
    private bool _fanCurveEnabled => _fanCurveOwner != FanCurveOwner.None;

    /// <summary>True while a curve is armed, whoever armed it.</summary>
    internal bool IsFanCurveArmed => _fanCurveEnabled;

    /// <summary>True while a running game profile owns the curve.</summary>
    internal bool IsGameFanCurveArmed => _fanCurveOwner == FanCurveOwner.Game;

    private FanCurveEvaluator? _fanCurveEvaluator;
    private DispatcherTimer? _fanCurveApplyTimer;
    private int _fanCurveLastTemperatureC;
    private int _fanCurveLastPercent;

    /// <summary>
    /// Watches for a fan that has stopped while its die is hot. Fed only by a real AASSvc sample: the fallback
    /// path reports 0 RPM because nothing measured the fans, and judging that would fire on every machine without
    /// the service.
    /// </summary>
    private readonly FanStallWatchdog _fanStallWatchdog = new();

    /// <summary>
    /// True while neither temperature sensor is answering. The curve then holds full speed instead of the
    /// speed the last good reading asked for, and the status line says why.
    /// </summary>
    private bool _fanCurveSensorDead;
    private bool _isUpdatingFanCurveUi;

    /// <summary>True while code (not the user) moves the fan-stall protection checkbox.</summary>
    private bool _isSyncingFanStallProtectionUi;

    /// <summary>The six sliders of the curve editor, in anchor order. Null before the window is built.</summary>
    private Slider?[] FanCurveSliders => new Slider?[] { SliderCurve0, SliderCurve1, SliderCurve2, SliderCurve3, SliderCurve4, SliderCurve5 };

    private TextBlock?[] FanCurveLabels => new TextBlock?[] { TxtCurve0, TxtCurve1, TxtCurve2, TxtCurve3, TxtCurve4, TxtCurve5 };

    /// <summary>The template buttons, in the order the XAML declares them. Empty before the window is built.</summary>
    private IEnumerable<WpfButton> CurvePresetButtons =>
        PnlCurvePresets?.Children.OfType<WpfButton>() ?? Enumerable.Empty<WpfButton>();

    // ---------------------------------------------------------------- per-game fan curve editor

    private Slider?[] GameFanCurveSliders => new Slider?[] { SliderGameCurve0, SliderGameCurve1, SliderGameCurve2, SliderGameCurve3, SliderGameCurve4, SliderGameCurve5 };

    private TextBlock?[] GameFanCurveLabels => new TextBlock?[] { TxtGameCurve0, TxtGameCurve1, TxtGameCurve2, TxtGameCurve3, TxtGameCurve4, TxtGameCurve5 };

    private bool? _lastServiceTelemetryAvailable;
    private static string ServiceUnavailableText => Loc.T("-- (Servis yok)");

    /// <summary>Tells the user once when AASSvc telemetry is lost and once when it comes back.</summary>
    private int _consecutiveTelemetryFailures;

    // ==========================================
    // Telemetry History Graph & Live Analytics
    // ==========================================

    private static readonly TimeSpan ChartWindow = TimeSpan.FromSeconds(60);

    // ==========================================
    // GPU Operating Mode (MUX Switch) Control
    // ==========================================

    private int? _supportedGpuCapability;

    /// <summary>
    /// Modes this model offers, or null when the service did not say. Null means "show everything": a capability
    /// query that failed must never take a working control away from the user.
    /// </summary>
    private IReadOnlyList<OperatingMode>? _supportedModes;

    /// <summary>
    /// What the keyboard reported about itself, or null when the service did not answer. Null keeps the four-zone
    /// UI, because a failed query is not evidence that the keyboard is single-zone.
    /// </summary>
    private KeyboardCapability? _keyboardCapability;

    // Mode written to the firmware in this session that only takes effect after a restart.
    private GpuOperatingMode? _pendingGpuMode;

    // ==========================================
    // Enhanced Game Detection & Per-Game Profiles
    // ==========================================

    // ExeNames in the exact order shown in CmbConfiguredGames. The profile list in GameDetectionService
    // can change at any time (background scan, deletes), so the combo's SelectedIndex must never be used
    // to index a freshly fetched GetProfiles() list; resolve the selection through this snapshot instead.
    private List<string> _configuredGameExeNames = new();
    private List<GameProfile> _configuredGameProfiles = new();

    /// <summary>Set when the settings file had to be recovered; shown once after startup.</summary>
    private string? _settingsRecoveryNotice;

    /// <summary>Floor between two attempts, so a burst of input cannot turn into a HID hammer.</summary>
    private const long MouseRetryMinGapMs = 700;

    /// <summary>
    /// Attempts and spacing for a game profile switch. The mouse is in active use by then, so a short burst is
    /// enough; the old three attempts three seconds apart could all land while the mouse was still idle.
    /// </summary>
    private const int GameMouseRetryAttempts = 5;
    private const long GameMouseRetryDelayMs = 1500;

    private sealed class DesktopMouseRequest(int profile)
    {
        public int Profile { get; } = profile;
        public long NextAttemptAt { get; set; } = long.MaxValue;
        public int Attempts { get; set; }
        public long LastAttemptAt { get; set; }
        public long LastInputAtAttempt { get; set; }

        /// <summary>
        /// An attempt is on the wire. The activity shortcut can make a retry due at any moment, so without this
        /// a second attempt could start while the first is still waiting on the mouse.
        /// </summary>
        public bool InFlight { get; set; }
    }

    private DesktopMouseRequest? _pendingDesktopMouseRequest;


    private (int Generation, int Profile, int Remaining, long Due)? _pendingGameMouseProfile;

    private static void SetComboBoxByTag(WpfComboBox cmb, string tagValue)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (cmb.Items[i] is ComboBoxItem item && item.Tag is string tag)
            {
                if (tag.Equals(tagValue, StringComparison.OrdinalIgnoreCase))
                {
                    cmb.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private (bool Allowed, bool OnAc, OperatingMode Mode)? _lastModeAvailability;

    // True while code (not the user) moves the RGB sliders, so their ValueChanged handlers do not
    // fire a second lighting command for a change that is already being applied.
    private bool _isSyncingLightingUi;
    private DispatcherTimer? _lightingApplyTimer;
    private static readonly TimeSpan LightingApplyDebounce = TimeSpan.FromMilliseconds(250);

    private bool _isSyncingDirectionUi;

    // True while code (not the user) moves the default-mouse-profile combo. Without it the assignment in
    // InitializeDefaultMouseProfileUi fired the SelectionChanged handler, which saved the settings file a second
    // time and briefly replaced the badge with "press Apply" before the apply result overwrote it.
    private bool _isSyncingDefaultMouseProfileUi;

    // ==========================================
    // UI Event Handlers
    // ==========================================

    // Whether the window should come back maximized when shown from the tray / after minimize.
    private bool _restoreMaximized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnWindowStateChangedForChrome()
    {
        if (WindowState == WindowState.Maximized) _restoreMaximized = true;
        else if (WindowState == WindowState.Normal) _restoreMaximized = false;

        bool maximized = WindowState == WindowState.Maximized;
        if (TxtMaximizeGlyph != null) TxtMaximizeGlyph.Text = maximized ? "" : "";
        if (BtnMaximize != null) BtnMaximize.ToolTip = maximized ? Loc.T("Geri al") : Loc.T("Büyüt");
        // Windows draws no frame around a maximized window; drop the 1px border so content reaches the edges.
        if (RootBorder != null) RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);

        if (WindowState != WindowState.Minimized && !_isInitializingUi)
        {
            SaveWindowPosition();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        SaveWindowPosition();
        Hide();
        TrimMemory();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowPosition();
        WindowState = WindowState.Minimized;
        TrimMemory();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowPosition();
        Hide();
        TrimMemory();
    }

    public void ShowWindow()
    {
        bool maximize = _restoreMaximized;
        Show();
        WindowState = maximize ? WindowState.Maximized : WindowState.Normal;
        UpdateTelemetryPollingRate();
        _ = ReassertKeyboardLightingAsync(isBoot: false);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // SW_RESTORE would un-maximize a maximized window, so bring it back in the state the user left it.
                ShowWindow(hwnd, maximize ? SW_SHOWMAXIMIZED : SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { }

        Activate();
        Focus();
        Topmost = true;
        Topmost = false;

        if (NavMonitoring?.IsChecked == true)
        {
            RenderTelemetryChart();
        }
    }

    private bool _exitRequested;
    public async void RealExit()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        try
        {
            if (_isGameSessionActive || HasPendingDesktopRestore)
            {
                await HandleGameEndedAsync(_gameService.ActiveGameProfile ?? new GameProfile());
                if (HasPendingDesktopRestore && !await RetryPendingDesktopRestoreAsync())
                {
                    ShowToast(Loc.T("Ayarlar geri yüklenemedi; yeniden deneyebilmeniz için uygulama açık bırakıldı."), false);
                    return;
                }
            }

            // Leave no battery Eco changes behind. If this fails, the persisted Eco state is restored on next start.
            if (_powerAutomationService != null && !await _powerAutomationService.RevertEcoForExitAsync())
            {
                AppLog.Warn("Çıkışta Eco tam geri alınamadı", "Temel durum diskte; bir sonraki açılışta geri yüklenecek.");
            }

            // The curve cannot run once the window is gone: hand the fans back to the firmware instead of leaving
            // them pinned at whatever speed the last reading asked for. The setting stays on, so the next start
            // takes the fans again.
            if (_fanCurveEnabled)
            {
                if (await _agentClient.SetFanModeAsync(FanMode.Auto))
                {
                    _currentFanMode = FanMode.Auto;
                    _powerAutomationService?.UpdateManualFanMode(FanMode.Auto);
                }
                else
                {
                    AppLog.Warn("Fan eğrisi", "Çıkışta fanlar otomatik moda döndürülemedi.");
                }
            }

            FinishExit();
        }
        catch (Exception ex) { ShowToast(Loc.T($"Çıkış tamamlanamadı: {ex.Message}"), false); }
        finally { _exitRequested = false; }
    }
    private void FinishExit()
    {
        _isExiting = true;
        CancelActiveLightingReassertion();
        CancelActiveOperatingModeReassertion();
        SaveWindowPosition();

        // The fans are on the firmware and the settings are on disk: this run ended on purpose, so the next start
        // must not reset the fan mode behind the user's back.
        CleanExitMarker.MarkCleanExit();

        _batteryService.StatusUpdated -= OnBatteryStatusUpdated;
        _batteryService.OverchargeAlert -= OnOverchargeAlert;
        _batteryService.DischargeTargetReached -= OnDischargeTargetReached;
        _batteryService.DischargeStateChanged -= OnDischargeStateChanged;
        _gameService.GameStarted -= OnGameStarted;
        _gameService.GameEnded -= OnGameEnded;

        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        _agentClient.ServiceEventReceived -= OnAgentServiceEvent;

        _pollTimer?.Stop();
        _toastTimer?.Stop();

        _hardwareService.Dispose();
        if (_osdWindow != null)
        {
            _osdWindow.IsExiting = true;
            _osdWindow.Close();
        }
        _powerAutomationService?.Dispose();
        _cpuPowerService.Dispose();
        _gameService.Dispose();
        _batteryService.Dispose();
        _agentClient.Dispose();
        _notifyIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void FastExitForSessionEnding()
    {
        _isExiting = true;
        try
        {
            SaveWindowPosition();

            // Windows is taking the process down and the normal exit path never runs. The curve cannot run once we
            // are gone, so hand the fans back to the firmware first, or they stay pinned at whatever speed the last
            // reading asked for. Blocking here is deliberate: the write has to land before the process does.
            // Task.Run keeps the continuations off this thread, which is the UI thread and about to be blocked, so
            // the wait cannot deadlock on the dispatcher.
            // The budget has to clear the 3 s connect timeout: a shorter wait returns while the connection is still
            // being made, and the Dispose below then closes the socket before the command is sent.
            if (_fanCurveEnabled)
            {
                try
                {
                    Task.Run(async () => { await _agentClient.SetFanModeAsync(FanMode.Auto); })
                        .Wait(TimeSpan.FromSeconds(4));
                }
                catch { }
            }

            // The fans are back on the firmware, so the next start must not treat this as a crash and reset them
            // again. Written even when the fan call failed: the marker only records that this run ended on purpose.
            CleanExitMarker.MarkCleanExit();

            if (_osdWindow != null)
            {
                _osdWindow.IsExiting = true;
                _osdWindow.Close();
            }
            _pollTimer?.Stop();
            _toastTimer?.Stop();
            _hardwareService.Dispose();
            _powerAutomationService?.Dispose();
            _cpuPowerService.Dispose();
            _gameService.Dispose();
            _batteryService.Dispose();
            _agentClient.Dispose();
            _notifyIcon?.Dispose();
        }
        catch { }
    }

    private async void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || TabModes == null || TabMonitoring == null || TabRgb == null || TabBattery == null || TabDebloat == null) return;

        TabModes.Visibility = NavModes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabMonitoring.Visibility = NavMonitoring.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabRgb.Visibility = NavRgb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabBattery.Visibility = NavBattery.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabDebloat.Visibility = NavDebloat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavMonitoring.IsChecked == true)
        {
            RenderTelemetryChart();
        }

        if (NavDebloat.IsChecked == true)
        {
            // NitroSense may have been opened or closed, or services changed outside NitroLite, since the last look.
            await RefreshServiceListAsync();
        }

        if (NavBattery.IsChecked == true)
        {
            await RefreshBatteryHealthAsync();
            await RefreshStorageHealthUi();
            // Acer Care Center may have been installed since the last look; the checkbox stays disabled until a
            // probe succeeds, so retry while it is still marked unavailable.
            if (_chargeLimitAvailable == false) await SyncChargeLimitFromCareCenterAsync();
        }
    }

    // Raised on a thread-pool thread by AcerAgentClient. Events are read while a request is in flight,
    // so with 1-3 s telemetry polling they arrive within a few seconds.
    private void OnAgentServiceEvent(string function, System.Text.Json.Nodes.JsonNode? parameter)
    {
        if (_isExiting || Dispatcher.HasShutdownStarted) return;

        if (function.Equals(ModeSwitchKey.PressEvent, StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await HandleModeSwitchKeyAsync(); }
                catch (Exception ex) { ShowToast(Loc.T($"Mod tuşu işlenemedi: {ex.Message}"), false); }
            }));
        }
        else if (function.Equals(ModeSwitchKey.BacklightTimeoutEvent, StringComparison.OrdinalIgnoreCase))
        {
            int? status = null;
            try { status = parameter?["status"]?.GetValue<int>(); } catch { }
            if (status.HasValue)
            {
                Dispatcher.BeginInvoke(new Action(() => SetBacklightTimeoutCheckboxSilently(status.Value == 1)));
            }
        }
    }

    private int _modeSwitchKeyBusy;

    /// <summary>Current power source and battery level; replaceable in tests.</summary>
    internal Func<(bool OnAc, int? BatteryPercent)> PowerStateProvider { get; set; } = ReadSystemPowerState;

    private static (bool OnAc, int? BatteryPercent) ReadSystemPowerState()
    {
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            bool onAc = ps.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline;
            // BatteryLifePercent is 0..1, or 255 (2.55) when unknown / no battery.
            int? percent = ps.BatteryLifePercent >= 0f && ps.BatteryLifePercent <= 1f
                ? (int)Math.Round(ps.BatteryLifePercent * 100f)
                : null;
            return (onAc, percent);
        }
        catch
        {
            return (true, null);
        }
    }

    private async Task HandleModeSwitchKeyAsync()
    {
        // NitroSense handles the same PRESS_TURBO_BUTTON event and switches the mode itself; handling it here too
        // would move two steps per key press. Let NitroSense own the key while it runs.
        if (_debloatService.IsNitroSenseRunning()) return;

        // Ignore key repeats while the previous switch is still being applied.
        if (Interlocked.Exchange(ref _modeSwitchKeyBusy, 1) != 0) return;
        try
        {
            var current = await _agentClient.TryGetOperatingModeAsync() ?? _currentMode;
            var (onAc, percent) = PowerStateProvider();
            var next = ModeSwitchKey.Next(current, onAc, percent);
            await SetManualModeAsync(next);
        }
        finally
        {
            Interlocked.Exchange(ref _modeSwitchKeyBusy, 0);
        }
    }

    private bool _isSyncingBacklightTimeoutUi;

    private readonly CareCenterClient _careCenterClient = new();
    private bool _isSyncingWatchdogUi;

    /// <summary>
    /// Whether AcerCCAgent answered at least once. Null until the first probe. False means the charge limit cannot
    /// be set on this machine, so the checkbox is disabled rather than left claiming a limit nothing enforces.
    /// </summary>
    private bool? _chargeLimitAvailable;

    private bool _serviceOperationBusy;

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAutoStart == null) return;
        bool enable = ChkAutoStart.IsChecked == true;

        // Started from inside the downloaded zip (or any temp folder): Windows deletes that copy later and the
        // autostart entry would point at nothing. Ask for a permanent folder instead of registering it.
        if (enable && DebloatService.IsTemporaryLocation(Environment.ProcessPath))
        {
            _isInitializingUi = true;
            try { ChkAutoStart.IsChecked = false; }
            finally { _isInitializingUi = false; }
            ShowToast(Loc.T("NitroLite geçici bir klasörden çalışıyor. Önce zip'i kalıcı bir klasöre çıkarın, sonra otomatik başlatmayı açın."), false);
            return;
        }

        if (_debloatService.SetNitroLiteAutoStart(enable, _appSettings.StartMinimized))
        {
            ShowToast(enable ? Loc.T("Windows açılışında otomatik çalıştırma etkin") : Loc.T("Windows açılışında çalıştırma kapatıldı"));
            return;
        }

        // Windows refused the change: show the real state again.
        _isInitializingUi = true;
        try { ChkAutoStart.IsChecked = _debloatService.IsNitroLiteAutoStartEnabled(); }
        finally { _isInitializingUi = false; }
        ShowToast(Loc.T("Başlangıç ayarı değiştirilemedi (kayıt defterine yazılamadı)."), false);
    }

    private void ChkStartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkStartMinimized == null) return;
        _appSettings.StartMinimized = ChkStartMinimized.IsChecked == true;
        SaveSettings();
        // The flag lives in the Run entry; only rewrite it when autostart is on.
        bool written = !_debloatService.IsNitroLiteAutoStartEnabled()
            || _debloatService.SetNitroLiteAutoStart(true, _appSettings.StartMinimized);
        if (!written)
        {
            ShowToast(Loc.T("Ayar kaydedildi ama başlangıç kaydı güncellenemedi."), false);
            return;
        }
        ShowToast(_appSettings.StartMinimized
            ? Loc.T("Windows açılışında pencere gösterilmeden tepside başlayacak")
            : Loc.T("Windows açılışında pencere açık başlayacak"));
    }

    private bool _ramCleanBusy;
    // True while the status line shows the outcome of the last clean. A flag, not a text search: the status
    // text is translated, so looking for a Turkish word only worked in Turkish.
    private bool _ramCleanResultShown;

    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _firstUpdateTimer;
    private UpdateInfo? _pendingUpdate;
    private bool _updateBusy;
    private bool _settingsCardReady;

}

