using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    internal FanMode SavedFanModeBeforeGame => _savedFanModeBeforeGame;
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
    internal int GameSessionGeneration => _gameSessionGeneration;
    internal SemaphoreSlim GameProfileTransitionLock => _gameProfileTransitionLock;
    internal System.Windows.Forms.ToolStripMenuItem? MenuItemRetryDesktop => _menuItemRetryDesktop;
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

    internal void SaveGameSessionRecoveryState(GameSessionRecoveryState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(GameSessionRecoveryFile, json);
        }
        catch (Exception ex)
        {
            // Without this file a crash mid-game leaves the game profile applied with nothing to undo it, so the
            // failure has to be in the log.
            AppLog.Warn("Oyun oturumu kurtarma durumu kaydedilemedi", $"{GameSessionRecoveryFile}: {ex.Message}");
        }
    }

    internal void ClearGameSessionRecoveryState()
    {
        try
        {
            if (File.Exists(GameSessionRecoveryFile))
            {
                File.Delete(GameSessionRecoveryFile);
            }
        }
        catch { }
    }

    internal GameSessionRecoveryState? LoadGameSessionRecoveryState()
    {
        try
        {
            if (File.Exists(GameSessionRecoveryFile))
            {
                var json = File.ReadAllText(GameSessionRecoveryFile);
                return JsonSerializer.Deserialize<GameSessionRecoveryState>(json);
            }
        }
        catch (Exception ex)
        {
            // A file that cannot be read means the app forgets it was in a game session; say so rather than
            // starting as if the last run ended cleanly.
            AppLog.Warn("Oyun oturumu kurtarma durumu okunamadı", $"{GameSessionRecoveryFile}: {ex.Message}");
        }
        return null;
    }
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
            _isInitializingUi = false;
            // The card reads the settings, so it is filled once they are loaded, not in the constructor.
            SetupSettingsCard();
        }
    }

    internal async Task<bool> RestoreInterruptedGameAsync(GameSessionRecoveryState recovery)
    {
        if (recovery.RestoreMode && await _agentClient.SetOperatingModeAsync(recovery.SavedMode))
        {
            recovery.RestoreMode = false;
            UpdateModeUi(recovery.SavedMode);
        }
        if (recovery.SavedAcBoost is { } ac &&
            await _cpuPowerService.SetBoostModeAsync(ac, targetAc: true, targetDc: false))
            recovery.SavedAcBoost = null;
        if (recovery.SavedDcBoost is { } dc &&
            await _cpuPowerService.SetBoostModeAsync(dc, targetAc: false, targetDc: true))
            recovery.SavedDcBoost = null;
        if (recovery.RestoreFan && await _agentClient.SetFanModeAsync(recovery.SavedFan))
        {
            recovery.RestoreFan = false;
            _currentFanMode = recovery.SavedFan;
            UpdateFanButtonsUi(recovery.SavedFan);
        }
        if (!_appSettings.AtkMouseIntegrationEnabled) recovery.SavedMouseProfile = 0;
        if (recovery.SavedMouseProfile is >= 1 and <= 4)
        {
            int target = _appSettings.DefaultAtkMouseProfile > 0
                ? _appSettings.DefaultAtkMouseProfile : recovery.SavedMouseProfile;
            if (await SwitchMouseProfileInternalAsync(target))
            {
                UpdateSideMouseProfileStatus(target);
                recovery.SavedMouseProfile = 0;
            }
        }
        else recovery.SavedMouseProfile = 0;

        var pending = new List<string>();
        if (recovery.RestoreMode) pending.Add(Loc.T("güç modu"));
        if (recovery.SavedAcBoost.HasValue) pending.Add("priz CPU boost");
        if (recovery.SavedDcBoost.HasValue) pending.Add("pil CPU boost");
        if (recovery.RestoreFan) pending.Add("fan modu");
        if (recovery.SavedMouseProfile > 0) pending.Add("fare profili");
        if (pending.Count == 0)
        {
            _interruptedGameRecovery = null;
            ClearGameSessionRecoveryState();
            ShowToast(Loc.T("Önceki oyun oturumundan doğrulanabilir ayarlar geri yüklendi."));
        }
        else
        {
            _nextGameRecoveryAttempt = Environment.TickCount64 + 10000;
            SaveGameSessionRecoveryState(recovery);
            ShowToast(Loc.T($"Geri yükleme bekleniyor: {string.Join(", ", pending)}."), false);
        }
        UpdateTrayRetryMenuItemState();
        return pending.Count == 0;
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

    /// <summary>
    /// The 60 Hz option only exists inside Eco: the battery branch of <see cref="PowerAutomationService"/> applies it
    /// there and nowhere else. Leaving it enabled under "Dengeli Mod" made it look like a setting that did nothing.
    /// </summary>
    private void UpdateDcRefreshOptionState()
    {
        if (ChkAutoRefreshRate == null) return;
        bool eco = _appSettings.DcPowerMode == DcPowerProfilePreference.Eco;
        ChkAutoRefreshRate.IsEnabled = eco;
        ChkAutoRefreshRate.Opacity = eco ? 1.0 : 0.45;
    }

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

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // Windows can cut power to the process at any point after this event, so anything still only in memory
            // is lost. Settings are normally written on every change, but the window rectangle is not: a user who
            // moves the window and closes the lid would come back to the old position.
            try
            {
                SaveWindowPosition();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Suspend: could not persist window position", ex.Message);
            }
            return;
        }

        if (e.Mode == PowerModes.Resume)
        {
            _ = ReassertKeyboardLightingAsync();
            _ = ReassertOperatingModeAsync(isBoot: false);
            // The adapter may have been plugged or unplugged while suspended, and Windows does not always raise
            // StatusChange on resume: re-evaluate the power state explicitly.
            _ = _powerAutomationService?.EvaluatePowerStateAsync();
            // The charge limit lives in the EC and is reset by some BIOS revisions across a suspend cycle. The
            // care-centre read-back is the only place that notices, so it has to run on resume too.
            _ = SyncChargeLimitFromCareCenterAsync();
            // A laptop that sleeps for days would otherwise skip the wear history entirely: the reading is only
            // taken at launch and when the battery page is opened. The history decides whether this one is new.
            _ = RefreshBatteryHealthAsync();
        }
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
        {
            _ = ReassertKeyboardLightingAsync();
            _ = ReassertOperatingModeAsync(isBoot: false);
        }
    }

    private CancellationTokenSource? _lightingReassertCts;

    private CancellationTokenSource? _modeReassertCts;

    public void CancelActiveOperatingModeReassertion()
    {
        try
        {
            _modeReassertCts?.Cancel();
            _modeReassertCts?.Dispose();
            _modeReassertCts = null;
        }
        catch { }
    }

    internal async Task RestoreStartupOperatingModeAsync()
    {
        OperatingMode? modeToApply = null;
        OperatingMode? preferredMode = null;
        bool ecoBaselinePending = _powerAutomationService is { } pendingAutomation &&
                                  (pendingAutomation.IsDcEcoActive || pendingAutomation.HasPendingAcRestore);

        if (_appSettings.LastOperatingMode.HasValue)
        {
            var preferred = _appSettings.LastOperatingMode.Value;
            preferredMode = preferred;
            var (onAc, percent) = PowerStateProvider();
            var targetMode = ModePolicy.ClampForPower(preferred, onAc, percent);

            // Inform PowerAutomationService of the preferred AC baseline. Skip it while a recovered battery Eco
            // baseline is still pending: UpdateManualOperatingMode clears the pending restore flags and would
            // drop the desktop settings the Eco pass has to put back.
            if (!ecoBaselinePending)
            {
                _powerAutomationService?.UpdateManualOperatingMode(preferred);
            }

            bool restored = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var cur = await _agentClient.TryGetOperatingModeAsync();
                if (cur.HasValue && cur.Value == targetMode)
                {
                    restored = true;
                    modeToApply = targetMode;
                    break;
                }

                if (await _agentClient.SetOperatingModeAsync(targetMode))
                {
                    restored = true;
                    modeToApply = targetMode;
                    break;
                }

                await Task.Delay(250);
            }

            if (!restored)
            {
                modeToApply = await _agentClient.TryGetOperatingModeAsync();
            }
        }
        else
        {
            modeToApply = await _agentClient.TryGetOperatingModeAsync();
            if (modeToApply.HasValue)
            {
                _appSettings.LastOperatingMode = modeToApply.Value;
                SaveSettings();
            }
        }

        if (modeToApply.HasValue)
        {
            UpdateModeUi(modeToApply.Value);
            // The desktop baseline is the user's preferred mode, not the value clamped for the current power
            // source: on battery Turbo is applied as Default, but plugging in must restore Turbo. Skip it while
            // a recovered Eco baseline is pending so the pending restore flags survive.
            if (!ecoBaselinePending)
            {
                UpdateDesktopBaselineForManualChange(mode: preferredMode ?? modeToApply.Value);
            }
        }
        else
        {
            ShowUnknownModeUi();
        }
    }

    public async Task ReassertOperatingModeAsync(bool isBoot = false)
    {
        if (!_appSettings.LastOperatingMode.HasValue) return;
        if (_debloatService.IsNitroSenseRunning()) return;

        CancelActiveOperatingModeReassertion();
        var cts = new CancellationTokenSource();
        _modeReassertCts = cts;
        var token = cts.Token;

        int[] delays = isBoot
            ? new[] { 1000, 3000, 7000, 15000 }
            : new[] { 500, 1500 };

        try
        {
            foreach (var delay in delays)
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested || _isExiting || _isGameSessionActive) return;

                var (onAc, percent) = PowerStateProvider();
                var targetMode = ModePolicy.ClampForPower(_appSettings.LastOperatingMode.Value, onAc, percent);

                // The same lock PowerAutomationService takes. Without it this reassert and an Eco pass started by
                // the same resume event both wrote the operating mode, and the hardware kept whichever landed last
                // while the two components each believed their own value had won.
                if (!await _gameProfileTransitionLock.WaitAsync(TimeSpan.FromSeconds(5), token)) continue;
                try
                {
                    var currentMode = await _agentClient.TryGetOperatingModeAsync();
                    if (currentMode.HasValue && currentMode.Value != targetMode)
                    {
                        if (await _agentClient.SetOperatingModeAsync(targetMode))
                        {
                            if (!_isExiting && !Dispatcher.HasShutdownStarted)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    UpdateModeUi(targetMode);
                                    UpdateDesktopBaselineForManualChange(mode: targetMode);
                                });
                            }
                        }
                    }
                }
                finally
                {
                    _gameProfileTransitionLock.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Trace only reaches a debugger; a mode that silently failed to reassert is exactly what a bug report
            // needs to show.
            AppLog.Warn("Çalışma modu yeniden uygulanamadı", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_modeReassertCts, cts))
            {
                _modeReassertCts = null;
            }
        }
    }

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
    /// True while neither temperature sensor is answering. The curve then holds full speed instead of the
    /// speed the last good reading asked for, and the status line says why.
    /// </summary>
    private bool _fanCurveSensorDead;
    private bool _isUpdatingFanCurveUi;

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
    // CPU Boost (Module B)
    // ==========================================

    private void UpdateBoostUi(CpuBoostMode mode)
    {
        _lastShownBoost = mode;

        // Values NitroLite does not set itself (Windows default 1, or 3/5/6) are named as such instead of being
        // shown as "Aggressive".
        string name = ModePolicy.BoostDisplayName(mode);
        // A boost level is a setting, not an alarm: it is set in ink like every other readout.
        var brush = TryFindResource("InkSoft") as SolidColorBrush ?? System.Windows.Media.Brushes.Gray;

        if (TxtBoostStatusBadge != null)
        {
            TxtBoostStatusBadge.Text = $" ({name})";
            TxtBoostStatusBadge.Foreground = brush;
        }

        if (TxtSideCpuBoost != null)
        {
            TxtSideCpuBoost.Text = (int)mode is 0 or 1 or 2 or 4 ? name : Loc.T("Diğer");
            TxtSideCpuBoost.Foreground = brush;
        }

        // Highlight the matching boost button (none for values without a button).
        SetSegActive(BtnBoostAggressive, mode == CpuBoostMode.Aggressive);
        SetSegActive(BtnBoostEfficient, mode == CpuBoostMode.Efficient);
        SetSegActive(BtnBoostDisabled, mode == CpuBoostMode.Disabled);
    }

    private void UpdateDesktopBaselineForManualChange(
        OperatingMode? mode = null,
        CpuBoostMode? boost = null,
        FanMode? fan = null)
    {
        if (_isGameSessionActive) return;

        if (mode.HasValue)
        {
            _savedModeBeforeGame = mode.Value;
            _pendingModeRestore = false;
            _powerAutomationService?.UpdateManualOperatingMode(mode.Value);
            if (!_isInitializingUi && _settingsLoaded)
            {
                _appSettings.LastOperatingMode = mode.Value;
                SaveSettings();
            }
        }

        if (boost.HasValue)
        {
            _savedCpuBoostBeforeGame = boost.Value;
            _pendingCpuBoostRestore = false;
            _powerAutomationService?.UpdateManualCpuBoost(boost.Value);
        }

        if (fan.HasValue)
        {
            _savedFanModeBeforeGame = fan.Value;
            _pendingFanRestore = false;
            _powerAutomationService?.UpdateManualFanMode(fan.Value);
        }

        // Recomputed from the individual steps, not from HasPendingDesktopRestore: that property includes
        // _hasPendingDesktopRestore itself, so assigning it here could never clear the flag after a failed restore.
        _hasPendingDesktopRestore = _interruptedGameRecovery != null ||
                                    _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore ||
                                    _pendingMouseRestore || (_powerAutomationService?.HasPendingAcRestore == true);
        // A manual change can satisfy the last outstanding step. The crash record has to go with it, or the next
        // start would restore the pre-game values over the choice the user just made.
        if (!HasPendingDesktopRestore)
        {
            _hasGameBaselineCaptured = false;
            _activeGameRecovery = null;
            ClearGameSessionRecoveryState();
        }
        UpdateTrayRetryMenuItemState();
    }

    internal async Task<bool> SetManualCpuBoostAsync(CpuBoostMode mode)
    {
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            bool ok = await ApplyCpuBoostModeAsync(mode);
            if (ok)
            {
                UpdateDesktopBaselineForManualChange(boost: mode);
            }
            return ok;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnBoost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is WpfButton btn && btn.Tag is string tag)
            {
                if (Enum.TryParse<CpuBoostMode>(tag, out var mode))
                {
                    await SetManualCpuBoostAsync(mode);
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"CPU modu uygulanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// Restores CPU boost after a game. On battery Eco the game's value is replaced by the desktop baseline on the
    /// AC power-plan value and by Disabled on the DC value only, so the adapter never inherits the Eco limit.
    /// </summary>
    private async Task<bool> ApplyRestoreBoostAsync(CpuBoostMode target, bool isBatteryEco)
    {
        if (!isBatteryEco)
        {
            if (_activeGameRecovery is { } baseline)
            {
                if (baseline.SavedAcBoost is { } common && baseline.SavedDcBoost == common)
                    return await ApplyCpuBoostModeAsync(common);
                bool restoreAc = baseline.SavedAcBoost is not { } savedAc ||
                    await _cpuPowerService.SetBoostModeAsync(savedAc, targetAc: true, targetDc: false);
                bool restoreDc = baseline.SavedDcBoost is not { } savedDc ||
                    await _cpuPowerService.SetBoostModeAsync(savedDc, targetAc: false, targetDc: true);
                if (restoreAc && restoreDc) UpdateBoostUi(_cpuPowerService.CurrentMode);
                return restoreAc && restoreDc;
            }
            return await ApplyCpuBoostModeAsync(target);
        }

        bool acOk = await _cpuPowerService.SetBoostModeAsync(_savedCpuBoostBeforeGame, targetAc: true, targetDc: false);
        bool dcOk = await _cpuPowerService.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: false, targetDc: true);
        if (acOk && dcOk)
        {
            UpdateBoostUi(_cpuPowerService.CurrentMode);
            return true;
        }
        ShowToast(Loc.T("CPU boost modu geri yüklenemedi."), false);
        return false;
    }

    private async Task<bool> ApplyCpuBoostModeAsync(CpuBoostMode mode)
    {
        var ok = await _cpuPowerService.SetBoostModeAsync(mode);
        if (ok)
        {
            UpdateBoostUi(mode);
            ShowToast(Loc.T($"CPU boost modu: {ModePolicy.BoostDisplayName(mode)}"));
            return true;
        }
        else
        {
            ShowToast(Loc.T($"CPU boost modu uygulanamadı: {ModePolicy.BoostDisplayName(mode)}"), false);
            return false;
        }
    }

    private async Task<bool> ApplyGameBoostModeAsync(CpuBoostMode mode)
    {
        if (_activeGameRecovery is not { } baseline) return await ApplyCpuBoostModeAsync(mode);
        bool ac = baseline.SavedAcBoost.HasValue;
        bool dc = baseline.SavedDcBoost.HasValue;
        if (!ac && !dc) return false;
        bool applied = await _cpuPowerService.SetBoostModeAsync(mode, targetAc: ac, targetDc: dc);
        if (applied) UpdateBoostUi(_cpuPowerService.CurrentMode);
        return applied;
    }

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

    public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode)    {
        int mask = 1 << (int)mode;
        return (capabilityBitmask & mask) != 0;
    }

    /// <summary>
    /// Mirrors NitroSense (personal-settings updateGPU_Mode): the GPU Operating card is shown whenever AASSvc reports
    /// a non-zero SUPPORT_GPU_MODE_CAPABILITY bitmask, and the options are the modes in that bitmask.
    /// NitroSense reads SUPPORT_GPU_MODE but never uses it to hide or lock the setting, so neither does NitroLite.
    /// An unknown capability (service not answering) never allows a switch.
    /// </summary>
    public static bool CanSwitchGpuMode(int? capabilityBitmask, GpuOperatingMode target)
    {
        return capabilityBitmask.HasValue
            && capabilityBitmask.Value > 0
            && IsGpuModeSupported(capabilityBitmask.Value, target);
    }

    private void SetGpuModeControlsEnabled(bool enabled, string? note)
    {
        if (CmbGpuMode != null) CmbGpuMode.IsEnabled = enabled;
        if (BtnApplyGpuMode != null) BtnApplyGpuMode.IsEnabled = enabled;
        if (TxtGpuModeNote != null)
        {
            TxtGpuModeNote.Text = note ?? string.Empty;
            TxtGpuModeNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public async Task RefreshGpuModeAsync()
    {
        try
        {
            var cap = await _agentClient.GetGpuModeCapabilityAsync();
            _supportedGpuCapability = cap;

            if (CmbGpuMode != null)
            {
                CmbGpuMode.Items.Clear();
            }

            if (cap.HasValue && cap.Value > 0 && CmbGpuMode != null)
            {

                // Bit 2 (4): Dynamic Display Switch (DDS / Advanced Optimus)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.Automatic))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.T("Otomatik Seçim (DDS / Advanced Optimus)"),
                        Tag = "Automatic"
                    });
                }

                // Bit 0 (1): Optimus (Hybrid iGPU + dGPU)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.Optimus))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.T("Optimus (Hibrit iGPU + dGPU)"),
                        Tag = "Optimus"
                    });
                }

                // Bit 1 (2): Discrete Only (dGPU)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.DiscreteGpu))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = Loc.T("Yalnızca Harici GPU (NVIDIA dGPU)"),
                        Tag = "DiscreteGpu"
                    });
                }
            }

            var mode = await _agentClient.GetGpuModeAsync();
            if (mode.HasValue)
            {
                UpdateGpuModeUi(mode.Value);
            }
            else
            {
                _currentGpuMode = null;
                if (TxtGpuModeBadge != null)
                {
                    TxtGpuModeBadge.Text = Loc.T("Bilinmiyor");
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("TextMuted");
                }
            }

            if (CmbGpuMode != null && CmbGpuMode.SelectedIndex == -1 && CmbGpuMode.Items.Count > 0)
            {
                CmbGpuMode.SelectedIndex = 0;
            }

            if (cap.HasValue && cap.Value > 0 && CmbGpuMode?.Items.Count > 0)
            {
                SetGpuModeControlsEnabled(true, Loc.T("Değişiklik, bilgisayar yeniden başlatıldıktan sonra geçerli olur."));
            }
            else if (cap.HasValue)
            {
                SetGpuModeControlsEnabled(false, Loc.T("Acer servisi bu cihaz için seçilebilir bir GPU modu bildirmiyor."));
            }
            else
            {
                SetGpuModeControlsEnabled(false,
                    Loc.T("GPU modu desteği Acer servisinden okunamadı; güvenlik için mod değiştirme kapalı."));
            }
        }
        catch (Exception ex)
        {
            _currentGpuMode = null;
            SetGpuModeControlsEnabled(false, Loc.T("GPU modu desteği Acer servisinden doğrulanamadı; güvenlik için mod değiştirme kapalı."));
            if (TxtGpuModeBadge != null)
            {
                TxtGpuModeBadge.Text = Loc.T("Bilinmiyor");
                TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("TextMuted");
            }
            // Trace only reaches a debugger. A GPU mode query that failed is exactly what a bug report needs to
            // show, so it goes to the same log the user can open from the settings card.
            AppLog.Warn("GPU modu okunamadı", ex.Message);
        }
    }

    private void UpdateGpuModeUi(GpuOperatingMode mode)
    {
        _currentGpuMode = mode;
        if (TxtGpuModeBadge != null)
        {
            switch (mode)
            {
                case GpuOperatingMode.DiscreteGpu:
                    TxtGpuModeBadge.Text = Loc.T("NVIDIA GPU Yalnızca (dGPU)");
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("AccentRed");
                    SelectGpuModeCombo("DiscreteGpu");
                    break;
                case GpuOperatingMode.Optimus:
                    TxtGpuModeBadge.Text = Loc.T("Optimus (Hibrit iGPU + dGPU)");
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                    SelectGpuModeCombo("Optimus");
                    break;
                case GpuOperatingMode.Automatic:
                default:
                    TxtGpuModeBadge.Text = Loc.T("Otomatik Seçim (DDS / Dynamic)");
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("AccentCyan");
                    SelectGpuModeCombo("Automatic");
                    break;
            }
        }
    }

    // Mode written to the firmware in this session that only takes effect after a restart.
    private GpuOperatingMode? _pendingGpuMode;

    private static string GpuModeShortName(GpuOperatingMode? mode) => mode switch
    {
        GpuOperatingMode.DiscreteGpu => Loc.T("Yalnızca NVIDIA"),
        GpuOperatingMode.Optimus => Loc.T("Optimus"),
        GpuOperatingMode.Automatic => Loc.T("Otomatik"),
        _ => Loc.T("Bilinmiyor")
    };

    private void ShowPendingGpuModeBadge()
    {
        if (TxtGpuModeBadge == null || !_pendingGpuMode.HasValue) return;
        TxtGpuModeBadge.Text = Loc.T($"{GpuModeShortName(_currentGpuMode)} → {GpuModeShortName(_pendingGpuMode)} (yeniden başlatma bekleniyor)");
        TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("AccentCyan");
    }

    private void SelectGpuModeCombo(string tag)
    {
        if (CmbGpuMode == null) return;
        for (int i = 0; i < CmbGpuMode.Items.Count; i++)
        {
            if (CmbGpuMode.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                CmbGpuMode.SelectedIndex = i;
                break;
            }
        }
    }

    private async void BtnApplyGpuMode_Click(object sender, RoutedEventArgs e)
    {
        if (CmbGpuMode?.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not string tagStr)
        {
            ShowToast(Loc.T("Lütfen bir GPU çalışma modu seçin."), false);
            return;
        }

        if (!Enum.TryParse<GpuOperatingMode>(tagStr, out var targetMode))
        {
            ShowToast(Loc.T("Geçersiz GPU modu."), false);
            return;
        }

        if (!CanSwitchGpuMode(_supportedGpuCapability, targetMode))
        {
            ShowToast(Loc.T("Seçilen GPU modu Acer servisinin bildirdiği modlar arasında değil."), false);
            return;
        }

        if (_pendingGpuMode.HasValue && targetMode == _pendingGpuMode.Value)
        {
            ShowToast(Loc.T("Bu GPU modu zaten ayarlandı; yeniden başlatınca geçerli olacak."));
            return;
        }

        // Re-selecting the running mode is allowed only to cancel a pending, not yet applied change.
        if (!_pendingGpuMode.HasValue && _currentGpuMode.HasValue && targetMode == _currentGpuMode.Value)
        {
            ShowToast(Loc.T("Seçilen GPU modu zaten aktif."));
            return;
        }

        string modeDisplayName = targetMode switch
        {
            GpuOperatingMode.DiscreteGpu => Loc.T("Yalnızca Harici GPU (NVIDIA dGPU)"),
            GpuOperatingMode.Optimus => Loc.T("Optimus (Hibrit iGPU + dGPU)"),
            _ => Loc.T("Otomatik Seçim (DDS / Advanced Optimus)")
        };

        var confirm = System.Windows.MessageBox.Show(
            this,
            Loc.T($"GPU Çalışma Modunu '{modeDisplayName}' olarak ayarlamak üzeresiniz.\n\n" +
                  "NitroSense'te olduğu gibi yeni mod, bilgisayar yeniden başlatıldıktan sonra geçerli olur.\n\n" +
                  "Devam etmek istiyor musunuz?"),
            Loc.T("GPU Çalışma Modu (MUX Switch)"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnApplyGpuMode.IsEnabled = false;
        try
        {
            var success = await _agentClient.SetGpuModeAsync(targetMode);
            if (success)
            {
                // The running mode does not change until restart: keep showing it and mark the pending one.
                if (_currentGpuMode.HasValue && targetMode == _currentGpuMode.Value)
                {
                    _pendingGpuMode = null;
                    UpdateGpuModeUi(targetMode);
                }
                else
                {
                    _pendingGpuMode = targetMode;
                    ShowPendingGpuModeBadge();
                }
                ShowToast(Loc.T($"GPU modu '{modeDisplayName}' olarak ayarlandı; yeniden başlatınca geçerli olacak."));

                var restart = System.Windows.MessageBox.Show(
                    this,
                    Loc.T("GPU modu kaydedildi. Değişikliğin geçerli olması için bilgisayarın yeniden başlatılması gerekiyor.\n\n" +
                    "Açık çalışmalarınızı kaydettiyseniz şimdi yeniden başlatılsın mı?"),
                    Loc.T("Yeniden Başlat ve Uygula"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (restart == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = AppPaths.SystemTool("shutdown.exe"),
                        Arguments = "/r /t 5 /c \"NitroLite: GPU modu degisikligi\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.Dispose();
                }
            }
            else
            {
                ShowToast(Loc.T("GPU modu uygulanamadı. Acer Agent Service yanıt vermedi."), false);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Hata: {ex.Message}"), false);
        }
        finally
        {
            BtnApplyGpuMode.IsEnabled = true;
        }
    }

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

    private string? GetSelectedGameExeName()
    {
        if (CmbConfiguredGames == null) return null;
        int idx = CmbConfiguredGames.SelectedIndex;
        var snapshot = _configuredGameExeNames;
        return idx >= 0 && idx < snapshot.Count ? snapshot[idx] : null;
    }

    private GameProfile? GetSelectedGameProfile()
    {
        if (CmbConfiguredGames == null) return null;
        int idx = CmbConfiguredGames.SelectedIndex;
        var snapshot = _configuredGameProfiles;
        return idx >= 0 && idx < snapshot.Count ? snapshot[idx] : null;
    }

    private void SelectConfiguredGame(string exeName)
    {
        if (CmbConfiguredGames == null) return;
        var idx = _configuredGameExeNames.FindIndex(e => e.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) CmbConfiguredGames.SelectedIndex = idx;
    }

    private void PopulateConfiguredGames(string? selectExeName = null)
    {
        if (CmbConfiguredGames == null) return;

        var profiles = _gameService.GetProfiles();
        var displayList = profiles.Select(p =>
        {
            var label = string.Equals(p.DisplayName, p.ExeName, StringComparison.OrdinalIgnoreCase)
                ? p.DisplayName
                : $"{p.DisplayName} ({p.ExeName})";
            // A profile whose exe is gone can never trigger; say so instead of silently doing nothing.
            return p.IsInstalled ? label : Loc.T($"{label} - kurulu değil");
        }).ToList();

        _isUpdatingGameUi = true;
        try
        {
            _configuredGameExeNames = profiles.Select(p => p.ExeName).ToList();
            _configuredGameProfiles = profiles;
            CmbConfiguredGames.ItemsSource = displayList;

            // Automatically prioritize currently running/active game if no specific selection was requested
            var targetExe = selectExeName ?? _gameService.ActiveGameProfile?.ExeName;

            if (!string.IsNullOrEmpty(targetExe))
            {
                var idx = profiles.FindIndex(p => p.ExeName.Equals(targetExe, StringComparison.OrdinalIgnoreCase));
                CmbConfiguredGames.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else if (CmbConfiguredGames.Items.Count > 0)
            {
                CmbConfiguredGames.SelectedIndex = 0;
            }
        }
        finally
        {
            _isUpdatingGameUi = false;
        }

        UpdateSelectedGameDetails();
    }

    private void CmbConfiguredGames_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingGameUi) return;
        UpdateSelectedGameDetails();
    }

    private void UpdateSelectedGameDetails()
    {
        var profile = GetSelectedGameProfile();
        if (profile == null) return;

        var isCurrentlyActive = _gameService.ActiveGameProfile != null &&
            _gameService.ActiveGameProfile.ExeName.Equals(profile.ExeName, StringComparison.OrdinalIgnoreCase);

        TxtSelectedGameNameBadge.Text = isCurrentlyActive 
            ? Loc.T($" [{profile.DisplayName} - ŞU AN OYNANIYOR]") 
            : $" [{profile.DisplayName}]";
        TxtSelectedGameNameBadge.Foreground = (SolidColorBrush)FindResource(isCurrentlyActive ? "AccentGreen" : "TextSecondary");
        var pathText = string.IsNullOrEmpty(profile.InstallPath)
            ? $"{profile.ExeName}.exe"
            : (profile.InstallPath.Length > 48 ? "..." + profile.InstallPath[^45..] : profile.InstallPath);
        TxtSelectedGameExePath.Text = profile.IsInstalled ? pathText : pathText + Loc.T("  (dosya bulunamadı)");

        // 1. Target Operating Mode ComboBox
        SetComboBoxByTag(CmbGameTargetMode, profile.TargetMode.ToString());

        // 2. Target CPU Boost ComboBox
        SetComboBoxByTag(CmbGameTargetCpuBoost, profile.TargetCpuBoost.ToString());

        // 3. Target Fan Mode ComboBox
        SetComboBoxByTag(CmbGameTargetFan, profile.TargetFanMode.ToString());

        // 4. Auto-apply toggle
        if (ChkGameAutoApply != null) ChkGameAutoApply.IsChecked = profile.AutoApply;

        // 5. Target ATK Mouse Profile ComboBox
        if (CmbGameAtkMouseProfile != null)
        {
            var targetIdx = profile.TargetAtkMouseProfile;
            CmbGameAtkMouseProfile.SelectedIndex = (targetIdx >= 0 && targetIdx < CmbGameAtkMouseProfile.Items.Count) 
                ? targetIdx 
                : 0;
        }

        // 6. Per-game fan curve
        LoadGameFanCurveEditor(profile);
    }

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

    private async void BtnSaveGameProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var stored = GetSelectedGameProfile();
            if (stored == null)
            {
                ShowToast(Loc.T("Seçili oyun profili bulunamadı; liste yenilendi."), false);
                PopulateConfiguredGames();
                return;
            }

            // Edit a copy so the shared instance held by GameDetectionService is only updated under its lock.
            var profile = new GameProfile
            {
                ExeName = stored.ExeName,
                DisplayName = stored.DisplayName,
                TargetMode = stored.TargetMode,
                TargetCpuBoost = stored.TargetCpuBoost,
                TargetFanMode = stored.TargetFanMode,
                InstallPath = stored.InstallPath,
                AutoApply = stored.AutoApply,
                TargetAtkMouseProfile = stored.TargetAtkMouseProfile,
                TargetFanCurveEnabled = stored.TargetFanCurveEnabled,
                TargetFanCurvePoints = stored.TargetFanCurvePoints
            };

            // 1. Operating Mode
            if (CmbGameTargetMode.SelectedItem is ComboBoxItem modeItem && modeItem.Tag is string modeTag)
            {
                if (Enum.TryParse<OperatingMode>(modeTag, out var m)) profile.TargetMode = m;
            }

            // 2. CPU Boost Mode
            if (CmbGameTargetCpuBoost.SelectedItem is ComboBoxItem cpuItem && cpuItem.Tag is string cpuTag)
            {
                if (Enum.TryParse<CpuBoostMode>(cpuTag, out var c)) profile.TargetCpuBoost = c;
            }

            // 3. Fan Mode
            if (CmbGameTargetFan.SelectedItem is ComboBoxItem fanItem && fanItem.Tag is string fanTag)
            {
                if (Enum.TryParse<FanMode>(fanTag, out var f)) profile.TargetFanMode = f;
            }

            // 4. Auto-apply toggle
            if (ChkGameAutoApply != null) profile.AutoApply = ChkGameAutoApply.IsChecked == true;

            // 5. ATK Mouse Profile
            if (CmbGameAtkMouseProfile != null && CmbGameAtkMouseProfile.SelectedIndex >= 0)
            {
                profile.TargetAtkMouseProfile = CmbGameAtkMouseProfile.SelectedIndex;
            }

            // 6. Per-game fan curve
            if (ChkGameFanCurve != null) profile.TargetFanCurveEnabled = ChkGameFanCurve.IsChecked == true;
            if (profile.TargetFanCurveEnabled)
            {
                var points = ReadGameFanCurveFromEditor();
                if (points != null) profile.TargetFanCurvePoints = points;
            }

            if (!_gameService.SaveProfile(profile))
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' ayarları uygulandı ama diske kaydedilemedi; uygulama kapanınca kaybolabilir."), false);
            }

            // If this game is currently running right now, hot-apply settings immediately in real-time!
            bool isCurrentlyRunning = _gameService.ActiveGameProfile != null && 
                                      _gameService.ActiveGameProfile.ExeName.Equals(profile.ExeName, StringComparison.OrdinalIgnoreCase);

            if (isCurrentlyRunning)
            {
                await ApplyLiveGameProfileAsync(profile);
            }
            else
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' oyun profili kaydedildi."));
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Profil kaydedilemedi: {ex.Message}"), false);
        }
    }

    internal async Task<bool> ApplyLiveGameProfileAsync(GameProfile profile)
    {
        int saveGen = Interlocked.Increment(ref _gameSessionGeneration);
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive || _gameService.ActiveGameProfile == null ||
                !_gameService.ActiveGameProfile.ExeName.Equals(profile.ExeName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var (liveOnAc, liveBattery) = PowerStateProvider();
            bool modeOk = await SwitchModeAsync(ModePolicy.ClampForPower(profile.TargetMode, liveOnAc, liveBattery));
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;

            bool boostOk = await ApplyGameBoostModeAsync(profile.TargetCpuBoost);
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;

            bool fanOk = profile.TargetFanCurveEnabled
                ? await ArmGameFanCurveAsync(profile)
                : await _agentClient.SetFanModeAsync(profile.TargetFanMode);
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;
            if (fanOk && !profile.TargetFanCurveEnabled)
            {
                _currentFanMode = profile.TargetFanMode;
            }

            bool mouseOk = true;
            if (_appSettings.AtkMouseIntegrationEnabled && profile.TargetAtkMouseProfile > 0)
            {
                var mouseResult = await TryApplyGameMouseProfileAsync(profile.TargetAtkMouseProfile, saveGen);
                mouseOk = mouseResult == AtkMouseResult.Confirmed;
                if (!mouseOk && mouseResult != AtkMouseResult.Rejected)
                    _pendingGameMouseProfile = (saveGen, profile.TargetAtkMouseProfile, GameMouseRetryAttempts, Environment.TickCount64 + GameMouseRetryDelayMs);
                if (mouseOk)
                {
                    _isMouseProfileSwitchedForGame = true;
                    UpdateSideMouseProfileStatus(profile.TargetAtkMouseProfile);
                }
                if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;
            }
            else if (_isMouseProfileSwitchedForGame)
            {
                // Profile option was changed to "Don't use" (0) or disabled while mouse had been switched for the game.
                // Restore mouse to desktop/default profile immediately!
                int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                    ? _appSettings.DefaultAtkMouseProfile 
                    : _savedMouseProfileBeforeGame;

                if (targetMouseProfile > 0)
                {
                    bool restoredOk = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                    if (restoredOk)
                    {
                        _isMouseProfileSwitchedForGame = false;
                        UpdateSideMouseProfileStatus(targetMouseProfile);
                    }
                    else
                    {
                        _isMouseProfileSwitchedForGame = true;
                        mouseOk = false;
                    }
                }
                if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;
            }
            else
            {
                _isMouseProfileSwitchedForGame = false;
            }

            var boostName = ModePolicy.BoostDisplayName(profile.TargetCpuBoost);
            var mouseInfo = (_isMouseProfileSwitchedForGame && profile.TargetAtkMouseProfile > 0) ? $" / ATK Fare: Config {profile.TargetAtkMouseProfile}" : "";

            bool allCoreOk = modeOk && boostOk && fanOk && mouseOk;
            if (TxtGameStatus != null)
            {
                if (allCoreOk)
                {
                    TxtGameStatus.Text = Loc.T($"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})");
                    if (TryFindResource("AccentGreen") is SolidColorBrush brush)
                    {
                        TxtGameStatus.Foreground = brush;
                    }
                }
                else
                {
                    TxtGameStatus.Text = Loc.T($"Kısmi Aktif: {profile.DisplayName.ToUpperInvariant()} (Bazı ayarlar uygulanamadı)");
                    if (TryFindResource("AccentRed") is SolidColorBrush brush)
                    {
                        TxtGameStatus.Foreground = brush;
                    }
                }
            }

            if (allCoreOk)
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' profili kaydedildi ve uygulandı."));
            }
            else
            {
                ShowToast(Loc.T($"'{profile.DisplayName}' profili kaydedildi ancak bazı donanım ayarları uygulanamadı."), false);
            }

            return allCoreOk;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnScanGames_Click(object sender, RoutedEventArgs e)
    {
        if (BtnScanGames != null)
        {
            BtnScanGames.IsEnabled = false;
            BtnScanGames.Content = Loc.T("Taranıyor…");
        }
        try
        {
            var found = await Task.Run(() => _gameService.ScanAndAddInstalledGames());
            PopulateConfiguredGames();
            ShowToast(found > 0 ? Loc.T($"{found} yeni oyun profili eklendi.") : Loc.T("Yeni yüklü oyun tespit edilmedi."));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Oyun taraması başarısız: {ex.Message}"), false);
        }
        finally
        {
            if (BtnScanGames != null)
            {
                BtnScanGames.Content = Loc.T("Yüklü Oyunları Tara");
                BtnScanGames.IsEnabled = true;
            }
        }
    }

    private void BtnAddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        var steamCommon = @"C:\Program Files (x86)\Steam\steamapps\common";
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Çalıştırılabilir Oyun Dosyasını Seçin"),
            Filter = Loc.T("Çalıştırılabilir Oyun Dosyaları (*.exe)|*.exe"),
            InitialDirectory = Directory.Exists(steamCommon) ? steamCommon : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dlg.ShowDialog(this) != true) return;

        var error = GameDetectionService.ValidateCustomGameExecutable(dlg.FileName);
        if (error != null)
        {
            ShowToast(error, false);
            return;
        }

        var exeName = Path.GetFileNameWithoutExtension(dlg.FileName);

        // Already configured: keep the user's settings instead of overwriting them with defaults.
        var existing = _gameService.GetProfile(exeName, dlg.FileName);
        if (existing != null)
        {
            PopulateConfiguredGames(existing.ExeName);
            ShowToast(Loc.T($"'{existing.DisplayName}' zaten listede; mevcut ayarları korundu."));
            return;
        }

        var cleanName = exeName.Replace("_", " ").Replace("-", " ");
        var profile = new GameProfile
        {
            ExeName = exeName,
            DisplayName = cleanName,
            InstallPath = dlg.FileName,
            TargetMode = OperatingMode.Turbo,
            // Same heuristic as the installed-games scan (Disabled only for light 2D games).
            TargetCpuBoost = GameDetectionService.DetermineOptimalCpuBoost(exeName),
            TargetFanMode = FanMode.Auto,
            AutoApply = true
        };

        bool saved = _gameService.SaveProfile(profile);
        PopulateConfiguredGames(exeName);
        ShowToast(saved
            ? Loc.T($"'{cleanName}' profili eklendi.")
            : Loc.T($"'{cleanName}' eklendi ama diske kaydedilemedi; uygulama kapanınca kaybolabilir."), saved);
    }

    private void BtnDeleteGame_Click(object sender, RoutedEventArgs e)
    {
        var profile = GetSelectedGameProfile();
        if (profile == null)
        {
            PopulateConfiguredGames();
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T($"'{profile.DisplayName}' oyun profilini silmek istiyor musunuz?"), 
            Loc.T("Profili Sil"), MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            bool deleted = _gameService.DeleteProfile(profile.ExeName, profile.InstallPath);
            PopulateConfiguredGames();
            ShowToast(deleted ? Loc.T($"'{profile.DisplayName}' profili silindi.")
                : Loc.T($"'{profile.DisplayName}' profili silinemedi."), deleted);
        }
    }

    private async void OnGameStarted(GameProfile profile)
    {
        try
        {
            await HandleGameStartedAsync(profile);
        }
        catch (Exception ex)
        {
            // Trace only reaches a debugger. A game profile that failed to apply is exactly what a bug report needs
            // to show, and the user can open this log from the settings card.
            AppLog.Warn("Oyun profili uygulanamadı", ex.ToString());
        }
    }

    internal async Task HandleGameStartedAsync(GameProfile profile)
    {
        if (Dispatcher.HasShutdownStarted) return;
        int sessionGen = Interlocked.Increment(ref _gameSessionGeneration);
        try
        {
            await _gameProfileTransitionLock.WaitAsync();
            try
            {
                if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                CancelActiveOperatingModeReassertion();

                Func<Task> work = async () =>
                {
                    if (Dispatcher.HasShutdownStarted || sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                    try
                    {
                        if (_interruptedGameRecovery is { } interrupted && !await RestoreInterruptedGameAsync(interrupted)) return;
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        // Automatically sync dropdown to currently running game
                        if (CmbConfiguredGames != null)
                        {
                            PopulateConfiguredGames(profile.ExeName);
                        }

                        // Save desktop baseline reference before the first game transition.
                        // Use _hasGameBaselineCaptured (not HasPendingDesktopRestore) so that a pending AC restore
                        // failure does NOT prevent capturing the real pre-game desktop baseline.
                        if (!_hasGameBaselineCaptured)
                        {
                            if (_powerAutomationService?.IsDcEcoActive == true)
                            {
                                bool isAc = _powerAutomationService.LastAcStatus;
                                _savedModeBeforeGame = (!isAc || _powerAutomationService.PendingAcModeRestore)
                                    ? _powerAutomationService.SavedOperatingMode
                                    : _currentMode;

                                _savedCpuBoostBeforeGame = (!isAc || _powerAutomationService.PendingAcCpuRestore)
                                    ? _powerAutomationService.SavedCpuBoost
                                    : _cpuPowerService.CurrentMode;

                                _savedFanModeBeforeGame = (!isAc || _powerAutomationService.PendingAcFanRestore)
                                    ? _powerAutomationService.SavedFanMode
                                    : _currentFanMode;
                            }
                            else
                            {
                                _savedModeBeforeGame = _currentMode;
                                // CurrentMode keeps its initial value when the query failed; ask the power plan
                                // directly instead of restoring a boost value the user never had.
                                _savedCpuBoostBeforeGame = _cpuPowerService.IsModeVerified
                                    ? _cpuPowerService.CurrentMode
                                    : (await Task.Run(() => _cpuPowerService.QueryBoostModeExact(true)) ?? _cpuPowerService.CurrentMode);
                                _savedFanModeBeforeGame = _currentFanMode;
                            }
                            _savedMouseProfileBeforeGame = _appSettings.DefaultAtkMouseProfile > 0
                                ? _appSettings.DefaultAtkMouseProfile : 0;
                            var (savedAc, savedDc) = await Task.Run(() =>
                                (_cpuPowerService.QueryBoostModeExact(true), _cpuPowerService.QueryBoostModeExact(false)));
                            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                            _activeGameRecovery = new GameSessionRecoveryState
                            {
                                IsActive = true,
                                SavedMode = _savedModeBeforeGame,
                                SavedBoost = _savedCpuBoostBeforeGame,
                                SavedAcBoost = savedAc,
                                SavedDcBoost = savedDc,
                                SavedFan = _savedFanModeBeforeGame,
                                SavedMouseProfile = _appSettings.AtkMouseIntegrationEnabled ? _savedMouseProfileBeforeGame : 0
                            };
                            _hasGameBaselineCaptured = true;
                        }

                        // Mark that this game session requires a desktop restore upon exit,
                        // separated from the one-time desktop baseline capture.
                        _hasPendingDesktopRestore = true;
                        _isGameSessionActive = true;
                        _batteryService.IsGameSessionActive = true;
                        UpdateTrayRetryMenuItemState();

                        if (_activeGameRecovery is { } baseline) SaveGameSessionRecoveryState(baseline);

                        var boostName = ModePolicy.BoostDisplayName(profile.TargetCpuBoost);

                        var mouseInfo = "";
                        bool mouseSwitchedThisTurn = false;
                        if (_appSettings.AtkMouseIntegrationEnabled && profile.TargetAtkMouseProfile > 0)
                        {
                            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                            var mouseResult = await TryApplyGameMouseProfileAsync(profile.TargetAtkMouseProfile, sessionGen);
                            if (mouseResult == AtkMouseResult.Confirmed)
                            {
                                _isMouseProfileSwitchedForGame = true;
                                mouseSwitchedThisTurn = true;
                                mouseInfo = $" / Fare: Config {profile.TargetAtkMouseProfile}";
                                UpdateSideMouseProfileStatus(profile.TargetAtkMouseProfile);
                            }
                            else
                            {
                                if (mouseResult != AtkMouseResult.Rejected)
                                    _pendingGameMouseProfile = (sessionGen, profile.TargetAtkMouseProfile, GameMouseRetryAttempts, Environment.TickCount64 + GameMouseRetryDelayMs);
                                ShowToast(Loc.T($"Oyun fare profili: {DescribeMouseFailure(mouseResult)}."), false);
                            }

                            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled)
                            {
                                if (mouseSwitchedThisTurn)
                                {
                                    _isMouseProfileSwitchedForGame = true;
                                    _pendingMouseRestore = true;
                                }
                                return;
                            }
                        }

                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        // Apply target settings with generation check at each stage under transition lock.
                        // Turbo/Performance are not allowed on battery or below 40 %: fall back to Balanced.
                        var (gameOnAc, gameBattery) = PowerStateProvider();
                        var gameMode = ModePolicy.ClampForPower(profile.TargetMode, gameOnAc, gameBattery);
                        if (gameMode != profile.TargetMode)
                        {
                            ShowToast(Loc.T($"'{profile.DisplayName}': {ModePolicy.DisplayName(profile.TargetMode)} pilde kullanılamaz, Dengeli uygulandı."), false);
                        }
                        bool modeOk = await SwitchModeAsync(gameMode);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        bool boostOk = await ApplyGameBoostModeAsync(profile.TargetCpuBoost);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        bool fanOk = profile.TargetFanCurveEnabled
                            ? await ArmGameFanCurveAsync(profile)
                            : await _agentClient.SetFanModeAsync(profile.TargetFanMode);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                        if (fanOk && !profile.TargetFanCurveEnabled)
                        {
                            _currentFanMode = profile.TargetFanMode;
                        }

                        bool allCoreOk = modeOk && boostOk && fanOk;
                        if (TxtGameStatus != null)
                        {
                            if (allCoreOk)
                            {
                                TxtGameStatus.Text = Loc.T($"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})");
                                if (TryFindResource("AccentGreen") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                            else
                            {
                                TxtGameStatus.Text = Loc.T($"Kısmi Aktif: {profile.DisplayName.ToUpperInvariant()} (Bazı ayarlar uygulanamadı)");
                                if (TryFindResource("AccentRed") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                        }

                        if (!allCoreOk)
                        {
                            ShowToast(Loc.T($"'{profile.DisplayName}' profilinin bazı donanım ayarları uygulanamadı."), false, trayBalloon: false);
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowToast(Loc.T($"Profil uygulanamadı: {ex.Message}"), false);
                    }
                };

                if (DispatcherRunner != null)
                {
                    await DispatcherRunner(work);
                }
                else
                {
                    var operation = Dispatcher.InvokeAsync(work);
                    await operation.Task.Unwrap();
                }
            }
            finally
            {
                _gameProfileTransitionLock.Release();
            }
        }
        catch (Exception ex)
        {
            // A game profile that fails to apply or undo leaves the machine in the wrong mode with no
            // other signal, so the reason has to reach the log.
            AppLog.Warn("Oyun profili uygulanamadı", ex.Message);
        }
    }

    private async void OnGameEnded(GameProfile profile)
    {
        try
        {
            await HandleGameEndedAsync(profile);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Oyun profili geri alınamadı", ex.ToString());
        }
    }

    internal (OperatingMode Mode, CpuBoostMode Boost, FanMode Fan, bool IsEco) GetEffectiveRestoreTargets()
    {
        bool isBatteryEco = (_powerAutomationService?.LastAcStatus == false) && (_appSettings.DcPowerMode == DcPowerProfilePreference.Eco);
        if (isBatteryEco)
        {
            return (OperatingMode.Quiet, CpuBoostMode.Disabled, FanMode.Auto, true);
        }
        return (_savedModeBeforeGame, _savedCpuBoostBeforeGame, _savedFanModeBeforeGame, false);
    }

    internal async Task HandleGameEndedAsync(GameProfile profile)
    {
        if (!_isGameSessionActive && _interruptedGameRecovery != null) return;
        if (Dispatcher.HasShutdownStarted) return;
        int endGen = Interlocked.Increment(ref _gameSessionGeneration);
        try
        {
            await _gameProfileTransitionLock.WaitAsync();
            try
            {
                if (endGen != _gameSessionGeneration) return;

                Func<Task> work = async () =>
                {
                    if (Dispatcher.HasShutdownStarted || endGen != _gameSessionGeneration) return;
                    try
                    {
                        // Immediately mark game session and battery monitoring inactive
                        _isGameSessionActive = false;
                        _batteryService.IsGameSessionActive = false;

                        if (HasPendingDesktopRestore)
                        {
                            // Pre-mark restore steps so that if interrupted at any step,
                            // unattempted steps remain pending and are not lost!
                            _pendingModeRestore = true;
                            _pendingCpuBoostRestore = true;
                            _pendingFanRestore = true;
                            if (_isMouseProfileSwitchedForGame || _pendingMouseRestore)
                            {
                                _pendingMouseRestore = true;
                            }
                            _hasPendingDesktopRestore = true;
                            UpdateTrayRetryMenuItemState();

                            var failedSteps = new List<string>();

                            var targets = GetEffectiveRestoreTargets();
                            OperatingMode targetRestoreMode = targets.Mode;
                            CpuBoostMode targetRestoreBoost = targets.Boost;
                            FanMode targetRestoreFan = targets.Fan;
                            bool isBatteryEco = targets.IsEco;

                            if (isBatteryEco && _powerAutomationService != null)
                            {
                                _powerAutomationService.SetSavedAcBaseline(_savedModeBeforeGame, _savedCpuBoostBeforeGame, _savedFanModeBeforeGame, force: false);
                                _powerAutomationService.SetDcEcoActive(true);
                            }

                            // 1. Operating mode
                            if (endGen != _gameSessionGeneration) return;
                            if (_pendingModeRestore)
                            {
                                bool modeOk = await SwitchModeAsync(targetRestoreMode);
                                if (endGen != _gameSessionGeneration) return;
                                if (modeOk)
                                {
                                    _pendingModeRestore = false;
                                }
                                else
                                {
                                    failedSteps.Add(Loc.T("Çalışma Modu"));
                                }
                            }

                            // 2. CPU Boost mode
                            if (endGen != _gameSessionGeneration) return;
                            if (_pendingCpuBoostRestore)
                            {
                                bool boostOk = await ApplyRestoreBoostAsync(targetRestoreBoost, isBatteryEco);
                                if (endGen != _gameSessionGeneration) return;
                                if (boostOk)
                                {
                                    _pendingCpuBoostRestore = false;
                                }
                                else
                                {
                                    failedSteps.Add("CPU Boost");
                                }
                            }

                            // 3. Fan mode (restore targetRestoreFan, NOT unconditionally Auto!)
                            if (endGen != _gameSessionGeneration) return;
                            if (_pendingFanRestore)
                            {
                                bool fanOk = await _agentClient.SetFanModeAsync(targetRestoreFan);
                                if (endGen != _gameSessionGeneration) return;
                                if (fanOk)
                                {
                                    _currentFanMode = targetRestoreFan;
                                    _pendingFanRestore = false;
                                }
                                else
                                {
                                    failedSteps.Add("Fan Modu");
                                }
                            }

                            // 4. Mouse profile
                            var restoredMouseInfo = "";
                            if (endGen != _gameSessionGeneration) return;
                            bool shouldRestoreMouse = _pendingMouseRestore || _isMouseProfileSwitchedForGame;
                            if (shouldRestoreMouse)
                            {
                                int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                                    ? _appSettings.DefaultAtkMouseProfile 
                                    : _savedMouseProfileBeforeGame;

                                if (targetMouseProfile > 0)
                                {
                                    if (endGen != _gameSessionGeneration) return;
                                    bool restoredOk = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                                    if (endGen != _gameSessionGeneration) return;
                                    if (restoredOk)
                                    {
                                        restoredMouseInfo = $" / Fare: Config {targetMouseProfile}";
                                        UpdateSideMouseProfileStatus(targetMouseProfile);
                                        _isMouseProfileSwitchedForGame = false;
                                        _pendingMouseRestore = false;
                                    }
                                    else
                                    {
                                        _isMouseProfileSwitchedForGame = true;
                                        _pendingMouseRestore = true;
                                        failedSteps.Add("Fare Profili");
                                        ShowToast(Loc.T("Varsayılan fare profiline geri dönülemedi."), false);
                                    }
                                }
                            }

                            if (endGen != _gameSessionGeneration) return;

                            bool allSucceeded = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore;
                            _hasPendingDesktopRestore = !allSucceeded;
                            if (allSucceeded)
                            {
                                _hasGameBaselineCaptured = false; // Allow next game start to capture a fresh baseline
                                _activeGameRecovery = null;
                                ClearGameSessionRecoveryState();
                            }

                            if (allSucceeded)
                            {
                                if (TxtGameStatus != null)
                                {
                                    TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                                        ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
                                        : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
                                    if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                                    {
                                        TxtGameStatus.Foreground = brush;
                                    }
                                }

                                string restoredSummary = isBatteryEco 
                                    ? Loc.T($"Pilde Eco profili (Quiet / Boost Devre Dışı{restoredMouseInfo})") 
                                    : Loc.T($"Varsayılan profil ({_savedModeBeforeGame}{restoredMouseInfo})");

                                // Game ended notification removed

                                ShowToast(isBatteryEco ? Loc.T("Pilde Eco profili uygulandı (Masaüstü ayarları adaptör için saklandı).") : Loc.T("Masaüstü ayarları başarıyla geri yüklendi."), trayBalloon: false);
                            }
                            else
                            {
                                if (TxtGameStatus != null)
                                {
                                    TxtGameStatus.Text = Loc.T("Durum: Masaüstü Geri Yükleme Bekliyor");
                                    if (TryFindResource("AccentCyan") is SolidColorBrush brush)
                                    {
                                        TxtGameStatus.Foreground = brush;
                                    }
                                }

                                ShowToast(Loc.T($"Geri yüklenemeyen ayarlar: {string.Join(", ", failedSteps)}"), false);
                            }
                        }
                        else if (_appSettings.AtkMouseIntegrationEnabled && _isMouseProfileSwitchedForGame)
                        {
                            if (endGen != _gameSessionGeneration) return;
                            int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                                ? _appSettings.DefaultAtkMouseProfile 
                                : _savedMouseProfileBeforeGame;
                            if (targetMouseProfile > 0)
                            {
                                bool restoredOk = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                                if (endGen != _gameSessionGeneration) return;
                                if (restoredOk)
                                {
                                    UpdateSideMouseProfileStatus(targetMouseProfile);
                                    _isMouseProfileSwitchedForGame = false;
                                    _pendingMouseRestore = false;
                                }
                                else
                                {
                                    _isMouseProfileSwitchedForGame = true;
                                    _pendingMouseRestore = true;
                                    ShowToast(Loc.T("Varsayılan fare profiline geri dönülemedi."), false);
                                }
                            }
                        }
                        else
                        {
                            if (TxtGameStatus != null)
                            {
                                TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                                    ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
                                    : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
                                if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                        }

                        // A curve the game profile armed hands the fans back here, after every restore step above, so
                        // the machine ends up on the desktop curve rather than on the game's last speed.
                        if (endGen != _gameSessionGeneration) return;
                        await ReleaseGameFanCurveAsync();
                        if (endGen != _gameSessionGeneration) return;

                        UpdateTrayRetryMenuItemState();

                        // Refresh dropdown badge
                        if (endGen == _gameSessionGeneration && CmbConfiguredGames != null)
                        {
                            PopulateConfiguredGames();
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowToast(Loc.T($"Profil geri yüklenemedi: {ex.Message}"), false);
                    }
                };

                if (DispatcherRunner != null)
                {
                    await DispatcherRunner(work);
                }
                else
                {
                    var operation = Dispatcher.InvokeAsync(work);
                    await operation.Task.Unwrap();
                }
            }
            finally
            {
                _gameProfileTransitionLock.Release();
            }
        }
        catch (Exception ex)
        {
            // A game profile that fails to apply or undo leaves the machine in the wrong mode with no
            // other signal, so the reason has to reach the log.
            AppLog.Warn("Oyun profili geri alınamadı", ex.Message);
        }
    }

    public async Task<bool> RetryPendingDesktopRestoreAsync()
    {
        if (!HasPendingDesktopRestore)
        {
            return true;
        }

        if (_isGameSessionActive) return false;

        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (_isGameSessionActive || !HasPendingDesktopRestore)
            {
                return !HasPendingDesktopRestore;
            }

            int retryGen = _gameSessionGeneration;

            Func<Task<bool>> work = async () =>
            {
                if (Dispatcher.HasShutdownStarted || _isGameSessionActive || retryGen != _gameSessionGeneration)
                    return false;

                if (_interruptedGameRecovery is { } interrupted)
                    return await RestoreInterruptedGameAsync(interrupted);

                var failed = new List<string>();
                var targets = GetEffectiveRestoreTargets();

                // 1. Operating mode
                if (_pendingModeRestore)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    bool ok = await SwitchModeAsync(targets.Mode);
                    if (ok)
                    {
                        _pendingModeRestore = false;
                    }
                    else
                    {
                        failed.Add(Loc.T("Çalışma Modu"));
                    }
                }

                // 2. CPU Boost
                if (_pendingCpuBoostRestore)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    bool ok = await ApplyRestoreBoostAsync(targets.Boost, targets.IsEco);
                    if (ok)
                    {
                        _pendingCpuBoostRestore = false;
                    }
                    else
                    {
                        failed.Add("CPU Boost");
                    }
                }

                // 3. Fan
                if (_pendingFanRestore)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    bool ok = await _agentClient.SetFanModeAsync(targets.Fan);
                    if (ok)
                    {
                        _currentFanMode = targets.Fan;
                        _pendingFanRestore = false;
                    }
                    else
                    {
                        failed.Add("Fan Modu");
                    }
                }

                // 4. Mouse
                if (_pendingMouseRestore || _isMouseProfileSwitchedForGame)
                {
                    if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;
                    int targetMouseProfile = _appSettings.DefaultAtkMouseProfile > 0 
                        ? _appSettings.DefaultAtkMouseProfile 
                        : _savedMouseProfileBeforeGame;

                    if (targetMouseProfile > 0)
                    {
                        bool ok = await SwitchMouseProfileInternalAsync(targetMouseProfile);
                        if (ok)
                        {
                            UpdateSideMouseProfileStatus(targetMouseProfile);
                            _isMouseProfileSwitchedForGame = false;
                            _pendingMouseRestore = false;
                        }
                        else
                        {
                            _isMouseProfileSwitchedForGame = true;
                            _pendingMouseRestore = true;
                            failed.Add("Fare Profili");
                        }
                    }
                }

                // 5. Pending AC Restore
                if (_powerAutomationService?.HasPendingAcRestore == true)
                {
                    bool acOk = await _powerAutomationService.RetryPendingAcRestoreAsync(acquireLock: false);
                    if (!acOk)
                    {
                        failed.Add(Loc.T("Adaptör Güç Ayarları"));
                    }
                }

                if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;

                bool allSucceeded = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore && (_powerAutomationService?.HasPendingAcRestore != true);
                _hasPendingDesktopRestore = !allSucceeded;

                if (allSucceeded)
                {
                    _hasGameBaselineCaptured = false;
                    _activeGameRecovery = null;
                    ClearGameSessionRecoveryState();
                    if (TxtGameStatus != null)
                    {
                        TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                            ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
                            : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
                        if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                        {
                            TxtGameStatus.Foreground = brush;
                        }
                    }
                    ShowToast(Loc.T("Bekleyen masaüstü ayarları başarıyla geri yüklendi."));
                }
                else
                {
                    if (TxtGameStatus != null)
                    {
                        TxtGameStatus.Text = Loc.T("Durum: Masaüstü Geri Yükleme Bekliyor");
                        if (TryFindResource("AccentCyan") is SolidColorBrush brush)
                        {
                            TxtGameStatus.Foreground = brush;
                        }
                    }
                    ShowToast(Loc.T($"Yeniden deneme tamamlanamadı: {string.Join(", ", failed)}"), false);
                }

                UpdateTrayRetryMenuItemState();
                return allSucceeded;
            };

            if (DispatcherRunner != null)
            {
                bool res = false;
                await DispatcherRunner(async () => { res = await work(); });
                return res;
            }
            else
            {
                var operation = Dispatcher.InvokeAsync(work);
                return await operation.Task.Unwrap();
            }
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private void ChkAutoGameMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ChkAutoGameMode == null) return;
        Interlocked.Increment(ref _gameSessionGeneration);
        _gameService.AutoGameModeEnabled = ChkAutoGameMode.IsChecked == true;
        if (!_isInitializingUi)
        {
            _appSettings.AutoGameModeEnabled = _gameService.AutoGameModeEnabled;
            SaveSettings();
        }
        TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
            ? Loc.T("Durum: Beklemede (Aktif oyun yok)") 
            : Loc.T("Durum: Devre Dışı (Manuel Kontrol)");
    }

    private void UpdateModeUi(OperatingMode mode)
    {
        bool modeChanged = _currentMode != mode;
        _currentMode = mode;
        if (modeChanged)
        {
            // The recording strip keeps a tick for every change inside its window, so a switch stays
            // visible on the paper instead of vanishing with the toast.
            var at = DateTime.Now;
            _modeMarks.Add((at, mode));
            var cutoff = at - ChartWindow;
            _modeMarks.RemoveAll(m => m.At < cutoff);
        }
        if (TxtModeHeader != null)
        {
            TxtModeHeader.Text = mode switch
            {
                OperatingMode.Quiet => Loc.T("SESSİZ"),
                OperatingMode.Default => Loc.T("DENGELİ"),
                OperatingMode.Extreme => Loc.T("PERFORMANS"),
                OperatingMode.Turbo => Loc.T("TURBO"),
                OperatingMode.Eco => Loc.T("ECO"),
                _ => ModePolicy.DisplayName(mode).ToUpperInvariant()
            };
        }

        // Reset all mode badges and card buttons
        ResetAllModeCards();

        // One signal colour for the whole board: the active row is marked by position and fill,
        // not by a per-mode hue, so the eye never has to relearn what a colour means.
        var signal = TryFindResource("Vermilion") as SolidColorBrush ?? System.Windows.Media.Brushes.Goldenrod;
        switch (mode)
        {
            case OperatingMode.Quiet:
                SetModeCardActive(BtnModeQuiet, IndQuiet, TxtIndQuiet, signal);
                break;
            case OperatingMode.Default:
                SetModeCardActive(BtnModeDefault, IndDefault, TxtIndDefault, signal);
                break;
            case OperatingMode.Extreme:
                SetModeCardActive(BtnModeExtreme, IndExtreme, TxtIndExtreme, signal);
                break;
            case OperatingMode.Turbo:
                SetModeCardActive(BtnModeTurbo, IndTurbo, TxtIndTurbo, signal);
                break;
            case OperatingMode.Eco:
                SetModeCardActive(BtnModeEco, IndEco, TxtIndEco, signal);
                break;
        }

        UpdateHighPowerAvailabilityUi();
    }

    private void ResetAllModeCards()
    {
        ResetModeCard(BtnModeQuiet, IndQuiet, TxtIndQuiet);
        ResetModeCard(BtnModeDefault, IndDefault, TxtIndDefault);
        ResetModeCard(BtnModeExtreme, IndExtreme, TxtIndExtreme);
        ResetModeCard(BtnModeTurbo, IndTurbo, TxtIndTurbo);
        ResetModeCard(BtnModeEco, IndEco, TxtIndEco);
    }

    private void ShowUnknownModeUi()
    {
        if (TxtModeHeader != null) TxtModeHeader.Text = Loc.T("BİLİNMİYOR");
        ResetAllModeCards();
        UpdateHighPowerAvailabilityUi();
    }

    private (bool Allowed, bool OnAc, OperatingMode Mode)? _lastModeAvailability;

    /// <summary>
    /// Asks the keyboard what it can do. The Nitro line ships four-zone RGB, single-zone RGB, single-colour
    /// backlight and no lighting at all; the four-zone picker only makes sense on the first. A null answer leaves
    /// the UI exactly as it was, because a failed query is not evidence that the keyboard is single-zone.
    /// </summary>
    public async Task RefreshKeyboardCapabilityAsync()
    {
        try
        {
            _keyboardCapability = await _agentClient.GetKeyboardCapabilityAsync();
        }
        catch
        {
            _keyboardCapability = null;
        }

        ApplyKeyboardCapabilityUi();
    }

    /// <summary>
    /// Hides the per-zone picker on a keyboard that cannot take a colour per zone. Only a positive answer from the
    /// device hides it: an unknown capability keeps the card, so a service hiccup never costs the user a control.
    /// </summary>
    private void ApplyKeyboardCapabilityUi()
    {
        if (CardZonePreview == null) return;

        var capability = _keyboardCapability;
        bool showZones = capability == null || capability.HasZoneColors;
        CardZonePreview.Visibility = showZones ? Visibility.Visible : Visibility.Collapsed;

        // The dropdown follows the same report: offering an effect the keyboard does not have only produces a
        // rejected command and a toast.
        RebuildEffectList();

        if (TxtKeyboardCapabilityNote == null) return;
        if (showZones)
        {
            TxtKeyboardCapabilityNote.Visibility = Visibility.Collapsed;
            return;
        }

        // The note sits outside the card on purpose: it explains the missing picker, so hiding the card must not
        // hide the explanation with it. It is a separate element from TxtZonePreviewNote, which carries the
        // per-effect notes and would otherwise overwrite this one on the next refresh.
        TxtKeyboardCapabilityNote.Text = capability!.HasLighting
            ? Loc.T("Bu klavye tek bölgeli; tüm tuşlar aynı rengi alır.")
            : Loc.T("Bu modelde klavye aydınlatması bildirilmedi.");
        TxtKeyboardCapabilityNote.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Asks the service which operating modes this model has. Nitro models differ: a Nitro V 15 without Turbo must
    /// not offer a Turbo button that the firmware rejects. A null answer leaves <see cref="_supportedModes"/> null,
    /// which <see cref="ModePolicy.IsModeSupported"/> reads as "unknown, show everything".
    /// </summary>
    public async Task RefreshModeCapabilityAsync()
    {
        try
        {
            _supportedModes = await _agentClient.GetOperatingModesAsync();
        }
        catch
        {
            _supportedModes = null;
        }

        // The cached availability tuple would short-circuit the redraw, so drop it before asking for one.
        _lastModeAvailability = null;
        UpdateHighPowerAvailabilityUi();
    }

    /// <summary>
    /// Shows each mode group only where it can be used: Turbo/Performance need the AC adapter (NitroSense also
    /// wants 40 % battery), Eco is a battery mode, and a mode the model does not offer is hidden outright. The row
    /// that is active stays visible even when its group is hidden, so the list always marks the mode the machine is
    /// actually in. Called on mode changes, battery status updates and the periodic state poll.
    /// </summary>
    private void UpdateHighPowerAvailabilityUi()
    {
        var (onAc, percent) = PowerStateProvider();
        bool allowed = ModePolicy.IsHighPowerAllowed(onAc, percent);

        var state = (allowed, onAc, _currentMode);
        if (_lastModeAvailability == state) return;
        _lastModeAvailability = state;

        var (showHighPower, showEco) = ModePolicy.VisibleModeGroups(onAc, _currentMode);

        // Per-mode, not per-group: a model can offer Performance and not Turbo.
        bool showExtreme = showHighPower && ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Extreme);
        bool showTurbo = showHighPower && ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Turbo);
        bool showQuiet = ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Quiet);
        bool showDefault = ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Default);
        bool showEcoMode = showEco && ModePolicy.IsModeSupported(_supportedModes, OperatingMode.Eco);

        foreach (var (card, visible) in new[]
                 {
                     (BtnModeQuiet, showQuiet),
                     (BtnModeDefault, showDefault),
                     (BtnModeExtreme, showExtreme),
                     (BtnModeTurbo, showTurbo)
                 })
        {
            if (card == null) continue;
            card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Only the high-power pair is gated on the adapter; Quiet and Balanced work on battery too.
        foreach (var card in new[] { BtnModeExtreme, BtnModeTurbo })
        {
            if (card == null) continue;
            card.IsEnabled = allowed;
            card.Opacity = allowed ? 1.0 : 0.45;
            card.ToolTip = allowed ? null : ModePolicy.HighPowerBlockedMessage;
        }

        if (BtnModeEco != null)
            BtnModeEco.Visibility = showEcoMode ? Visibility.Visible : Visibility.Collapsed;

        if (TxtModeLockNote != null)
        {
            TxtModeLockNote.Text = allowed ? string.Empty : ModePolicy.HighPowerBlockedMessage;
            TxtModeLockNote.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ResetModeCard(WpfButton? btn, Border? ind, TextBlock? txt)
    {
        if (ind != null)
        {
            ind.Background = System.Windows.Media.Brushes.Transparent;
            ind.BorderBrush = TryFindResource("Rule") as SolidColorBrush ?? System.Windows.Media.Brushes.Gray;
        }
        if (txt != null)
        {
            txt.Text = Loc.T("SEÇ");
            txt.Foreground = TryFindResource("TextMuted") as SolidColorBrush ?? System.Windows.Media.Brushes.Gray;
        }
        if (btn != null)
        {
            // The 3x18 mark in the row's left margin is driven by BorderBrush; transparent means "no mark".
            btn.Background = System.Windows.Media.Brushes.Transparent;
            btn.BorderBrush = System.Windows.Media.Brushes.Transparent;
            btn.Foreground = TryFindResource("TextSecondary") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
        }
    }

    /// <summary>
    /// The active mode is marked by a 3px vermilion time mark above its row, full ink type and a
    /// vermilion chip; the other rows drop back to soft ink. State by de-emphasis, never by a fill,
    /// so the eye never has to relearn what a colour means.
    /// </summary>
    private void SetModeCardActive(WpfButton? btn, Border? ind, TextBlock? txt, SolidColorBrush accent)
    {
        if (ind != null)
        {
            ind.Background = System.Windows.Media.Brushes.Transparent;
            ind.BorderBrush = accent;
        }
        if (txt != null)
        {
            txt.Text = Loc.T("AKTİF");
            txt.Foreground = accent;
        }
        if (btn != null)
        {
            btn.Background = System.Windows.Media.Brushes.Transparent;
            btn.BorderBrush = accent;
            btn.Foreground = TryFindResource("TextPrimary") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
        }
    }

    // True while code (not the user) moves the RGB sliders, so their ValueChanged handlers do not
    // fire a second lighting command for a change that is already being applied.
    private bool _isSyncingLightingUi;
    private DispatcherTimer? _lightingApplyTimer;
    private static readonly TimeSpan LightingApplyDebounce = TimeSpan.FromMilliseconds(250);

    private bool _isSyncingDirectionUi;

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

    internal async Task<bool> SetManualModeAsync(OperatingMode mode)
    {
        var (onAc, percent) = PowerStateProvider();
        if (!ModePolicy.IsModeAllowed(mode, onAc, percent))
        {
            ShowToast(ModePolicy.HighPowerBlockedMessage, false);
            return false;
        }

        CancelActiveOperatingModeReassertion();

        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            bool ok = await SwitchModeAsync(mode);
            if (ok)
            {
                _currentMode = mode;
                UpdateDesktopBaselineForManualChange(mode: mode);
                _appSettings.LastOperatingMode = mode;
                SaveSettings();
            }
            return ok;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnMode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is WpfButton btn && btn.Tag is string tagStr)
            {
                if (Enum.TryParse<OperatingMode>(tagStr, out var mode))
                {
                    await SetManualModeAsync(mode);
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Çalışma modu uygulanamadı: {ex.Message}"), false);
        }
    }

    private async Task<bool> SwitchModeAsync(OperatingMode mode)
    {
        try
        {
            var ok = await _agentClient.SetOperatingModeAsync(mode);
            if (ok)
            {
                UpdateModeUi(mode);
                ShowToast(Loc.T($"Çalışma modu: {ModePolicy.DisplayName(mode)}"), isSuccess: true, trayBalloon: false);
                return true;
            }
            else
            {
                ShowToast(Loc.T($"Çalışma modu değiştirilemedi: {ModePolicy.DisplayName(mode)}"), false);
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Çalışma modu hatası: {ex.Message}"), false);
            return false;
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

    private async void CmbDcPowerMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || CmbDcPowerMode?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (Enum.TryParse<DcPowerProfilePreference>(tag, out var pref))
        {
            _appSettings.DcPowerMode = pref;
            UpdateDcRefreshOptionState();
            SaveSettings();
            ShowToast(pref == DcPowerProfilePreference.Eco ? Loc.T("Pilde güç tercihi: Eco (sessiz mod, pilde boost kapalı)") : Loc.T("Pilde güç tercihi: Dengeli (mevcut ayarlar korunur)"));
            try
            {
                if (_powerAutomationService != null) await _powerAutomationService.EvaluatePowerStateAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Pil güç tercihi uygulanamadı", ex.ToString());
            }
        }
    }

    private async void ChkAutoRefreshRate_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAutoRefreshRate == null) return;
        _appSettings.AutoRefreshRateSwitching = ChkAutoRefreshRate.IsChecked == true;
        try
        {
            if (_powerAutomationService != null) await _powerAutomationService.EvaluatePowerStateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ekran yenileme hızı geçişi uygulanamadı", ex.ToString());
        }
        SaveSettings();
        ShowToast(_appSettings.AutoRefreshRateSwitching
            ? Loc.T("Otomatik tazeleme etkin: pilde Eco laptop ekranını 60 Hz'e alır, adaptörde önceki hıza döner")
            : Loc.T("Otomatik ekran tazeleme hızı geçişi devre dışı"));
    }

    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _firstUpdateTimer;
    private UpdateInfo? _pendingUpdate;
    private bool _updateBusy;
    private bool _settingsCardReady;

}

