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
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
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
    private AtkMouseService _atkMouseService;
    private PowerAutomationService? _powerAutomationService;

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
    internal bool IsGameSessionActive => _isGameSessionActive;
    internal int GameSessionGeneration => _gameSessionGeneration;
    internal SemaphoreSlim GameProfileTransitionLock => _gameProfileTransitionLock;
    internal System.Windows.Forms.ToolStripMenuItem? MenuItemRetryDesktop => _menuItemRetryDesktop;
    internal Func<Func<Task>, Task>? DispatcherRunner { get; set; }
    internal Func<int, bool>? MouseProfileSwitcher { get; set; }
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
    private bool _isUpdatingGameUi = false;
    private bool _isInitializingUi = true;
    private bool _isPolling = false;
    private bool _isExiting = false;

    private static readonly string ActiveRgbConfigFile = AppPaths.GetDataFile("active_rgb_state.json");
    private static readonly string AppSettingsConfigFile = AppPaths.GetDataFile("settings.json");
    private static readonly string LegacyConfigFile = AppPaths.GetDataFile("config.json");
    private static readonly string GameSessionRecoveryFile = AppPaths.GetDataFile("active_game_session.json");
    private static readonly string DcEcoStateFile = AppPaths.GetDataFile("dc_eco_state.json");

    internal void SaveGameSessionRecoveryState(GameSessionRecoveryState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            string tmp = GameSessionRecoveryFile + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            File.Move(tmp, GameSessionRecoveryFile, overwrite: true);
        }
        catch { }
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
        catch { }
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

    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        try
        {
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch { }
    }

    private int _hasInitialized = 0;

    public async Task EnsureInitializedAsync()
    {
        if (Interlocked.Exchange(ref _hasInitialized, 1) != 0) return;
        await RefreshAllAsync();
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
        _displayService = new DisplayRefreshRateService();
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
        HardwareMonitorService? hardwareService = null)
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
        _displayService = displayService ?? (isTestHarness ? new TestDisplayRefreshRateServiceStub() : new DisplayRefreshRateService());
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

        AddMode("Sessiz Mod", OperatingMode.Quiet);
        AddMode("Dengeli Mod", OperatingMode.Default);
        AddMode("Performans Modu", OperatingMode.Extreme);
        AddMode("Turbo Mod", OperatingMode.Turbo);
        AddMode("Eco Mod", OperatingMode.Eco);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddBoost(Loc.T("CPU Boost: Devre Dışı"), CpuBoostMode.Disabled);
        AddBoost("CPU Boost: Verimli", CpuBoostMode.Efficient);
        AddBoost("CPU Boost: Agresif", CpuBoostMode.Aggressive);

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
                System.Diagnostics.Trace.WriteLine("Recovered persisted battery Eco baseline.");
            }

            // 1. Initial Mode (Restore saved manual mode or query hardware)
            await RestoreStartupOperatingModeAsync();
            _ = ReassertOperatingModeAsync(isBoot: true);

            // 1.0 Initial fan mode: read it instead of assuming Auto (NitroSense may have left it on Max).
            var initialFan = await _agentClient.GetFanModeAsync();
            if (initialFan.HasValue) _currentFanMode = initialFan.Value;
            UpdateFanButtonsUi(_currentFanMode, initialFan.HasValue);

            // 1.05 Hardware names (CPU, GPU, memory) from Windows, read in the background
            _ = LoadHardwareInfoAsync();

            // 1.1 Initial GPU Mode (MUX Switch)
            await RefreshGpuModeAsync();

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
                    var savedRgb = JsonSerializer.Deserialize<LightingConfig>(json);
                    if (savedRgb != null)
                    {
                        _lighting = savedRgb;
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
                    _appSettings.SelectedRgbPreset = presetName;
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

    private void SaveActiveRgbState()
    {
        try
        {
            var json = JsonSerializer.Serialize(_lighting, new JsonSerializerOptions { WriteIndented = true });
            string tmp = ActiveRgbConfigFile + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            File.Move(tmp, ActiveRgbConfigFile, overwrite: true);
        }
        catch { }
    }

    private void OnSystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _ = ReassertKeyboardLightingAsync();
            _ = ReassertOperatingModeAsync(isBoot: false);
            // The adapter may have been plugged or unplugged while suspended, and Windows does not always raise
            // StatusChange on resume: re-evaluate the power state explicitly.
            _ = _powerAutomationService?.EvaluatePowerStateAsync();
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

    public void CancelActiveLightingReassertion()
    {
        try
        {
            _lightingReassertCts?.Cancel();
            _lightingReassertCts?.Dispose();
            _lightingReassertCts = null;
        }
        catch { }
    }

    public async Task ReassertKeyboardLightingAsync(bool isBoot = false)
    {
        // NitroSense also restores the keyboard lighting; re-applying ours on resume/unlock would fight it.
        if (_debloatService.IsNitroSenseRunning()) return;

        CancelActiveLightingReassertion();
        var cts = new CancellationTokenSource();
        _lightingReassertCts = cts;
        var token = cts.Token;

        // Staged re-assertion pulses with socket reconnect retry
        // to override Acer EC firmware default resets, AcerLightingService boot triggers, and sleep wakeups.
        int[] delays = isBoot
            ? new[] { 500, 2000, 5000, 10000, 18000 }
            : new[] { 250, 1500 };

        try
        {
            foreach (var delay in delays)
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested) return;

                for (int retry = 0; retry < 3; retry++)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        bool shouldBeOff = _appSettings.IsBacklightOff || 
                                           _lighting.Brightness == 0 || 
                                           _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
                                           (!string.IsNullOrEmpty(_appSettings.SelectedRgbPreset) && 
                                            _appSettings.SelectedRgbPreset.Contains("kapalı", StringComparison.OrdinalIgnoreCase));

                        bool success = shouldBeOff
                            ? await _agentClient.TurnOffLightingAsync()
                            : await _agentClient.SetLightingAsync(_lighting.Effect, _lighting.Brightness, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs.ToArray());

                        if (success)
                        {
                            break;
                        }

                        await Task.Delay(200, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        await Task.Delay(200, token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Gracefully canceled when user changed lighting or window reopened
        }
    }

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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"ReassertOperatingModeAsync error: {ex.Message}");
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

    /// <summary>Fills the CPU/GPU/memory names from Windows instead of hard-coded labels.</summary>
    private async Task LoadHardwareInfoAsync()
    {
        try
        {
            var info = await Task.Run(HardwareInfoService.Get);
            _hardwareInfo = info;
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (TxtMonCpuName != null && info.CpuName.Length > 0) TxtMonCpuName.Text = info.CpuName;
                if (TxtMonGpuName != null && info.DiscreteGpuName.Length > 0) TxtMonGpuName.Text = info.DiscreteGpuName;
                if (TxtMonRamInfo != null) TxtMonRamInfo.Text = info.FormatMemoryText();
            });
        }
        catch { }
    }

    /// <summary>Sets a bar value; if the value exceeds the bar's ceiling the ceiling grows instead of the bar pinning at full.</summary>
    private static void SetBarValueGrowing(System.Windows.Controls.ProgressBar? bar, double value, double step)
    {
        if (bar == null) return;
        if (value > bar.Maximum) bar.Maximum = Math.Ceiling(value / step) * step;
        bar.Value = Math.Max(0, value);
    }

    /// <summary>
    /// Temperature is the one metric where colour carries meaning rather than decoration: the bar and
    /// its reading turn red once the part runs hot, so the panel answers "is anything wrong" at a glance.
    /// </summary>
    private void ApplyTempSeverity(System.Windows.Controls.ProgressBar? bar, TextBlock? value, double celsius, bool available)
    {
        if (bar == null || value == null) return;
        bool hot = available && celsius >= 85;
        var hotBrush = TryFindResource("AccentRed") as SolidColorBrush ?? System.Windows.Media.Brushes.Red;
        var idleBrush = TryFindResource("AccentCyan") as SolidColorBrush ?? System.Windows.Media.Brushes.Goldenrod;
        bar.Foreground = hot ? hotBrush : idleBrush;
        value.Foreground = hot ? hotBrush : (TryFindResource("TextPrimary") as SolidColorBrush ?? System.Windows.Media.Brushes.White);
    }

    private DateTime _lastDeviceStateSyncUtc = DateTime.MinValue;
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
                if (!_isGameSessionActive) UpdateDesktopBaselineForManualChange(fan: fan.Value);
            }
            UpdateFanButtonsUi(fan ?? _currentFanMode, fan.HasValue);

            var boost = _cpuPowerService.QueryCurrentBoostMode();
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

    /// <summary>Highlights the active fan mode button (Auto / Max).</summary>
    private void UpdateFanButtonsUi(FanMode mode, bool known = true)
    {
        if (BtnFanAuto == null || BtnFanMax == null) return;
        SetSegActive(BtnFanAuto, known && mode == FanMode.Auto);
        SetSegActive(BtnFanMax, known && mode == FanMode.Max);
        BtnFanAuto.ToolTip = known && mode == FanMode.Auto ? Loc.T("Etkin") : null;
        BtnFanMax.ToolTip = known && mode == FanMode.Max ? Loc.T("Etkin") : null;
        if (BtnFanCustom != null)
        {
            bool custom = known && mode == FanMode.Custom;
            SetSegActive(BtnFanCustom, custom);
            BtnFanCustom.ToolTip = custom ? Loc.T("Etkin") : Loc.T("CPU ve GPU fanını sabit bir hızda (%30–100) çalıştırır.");
        }
        // The sliders stay open while the user is choosing "Özel" (before the service confirms it).
        if (PnlCustomFan != null)
            PnlCustomFan.Visibility = (known && mode == FanMode.Custom) || _customFanPanelPinned ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>Loads the saved custom speeds into the sliders and the Acer client (all restore paths use them).</summary>
    private void LoadCustomFanSettings()
    {
        int cpu = AppSettings.ClampCustomFanPercent(_appSettings.CustomFanCpuPercent);
        int gpu = AppSettings.ClampCustomFanPercent(_appSettings.CustomFanGpuPercent);
        _agentClient.CustomFanCpuPercent = cpu;
        _agentClient.CustomFanGpuPercent = gpu;
        if (SliderFanCpu != null) SliderFanCpu.Value = cpu;
        if (SliderFanGpu != null) SliderFanGpu.Value = gpu;
        UpdateCustomFanLabels();
    }

    private void UpdateCustomFanLabels()
    {
        if (TxtFanCpuPercent != null && SliderFanCpu != null) TxtFanCpuPercent.Text = $"%{(int)SliderFanCpu.Value}";
        if (TxtFanGpuPercent != null && SliderFanGpu != null) TxtFanGpuPercent.Text = $"%{(int)SliderFanGpu.Value}";
    }

    private async void BtnFanCustom_Click(object sender, RoutedEventArgs e)
    {
        _customFanPanelPinned = true;
        try
        {
            await SetManualFanModeAsync(FanMode.Custom);
        }
        finally
        {
            _customFanPanelPinned = false;
            UpdateFanButtonsUi(_currentFanMode);
        }
    }

    private void SliderCustomFan_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateCustomFanLabels();
        if (!IsLoaded || _isInitializingUi || SliderFanCpu == null || SliderFanGpu == null) return;

        _appSettings.CustomFanCpuPercent = AppSettings.ClampCustomFanPercent((int)SliderFanCpu.Value);
        _appSettings.CustomFanGpuPercent = AppSettings.ClampCustomFanPercent((int)SliderFanGpu.Value);
        _agentClient.CustomFanCpuPercent = _appSettings.CustomFanCpuPercent;
        _agentClient.CustomFanGpuPercent = _appSettings.CustomFanGpuPercent;

        // Apply and save once dragging pauses, not on every tick of the slider.
        _customFanApplyTimer ??= CreateCustomFanApplyTimer();
        _customFanApplyTimer.Stop();
        _customFanApplyTimer.Start();
    }

    private DispatcherTimer CreateCustomFanApplyTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (s, e) =>
        {
            timer.Stop();
            SaveSettings();
            if (_currentFanMode != FanMode.Custom || _isGameSessionActive) return;
            if (!await _agentClient.SetFanModeAsync(FanMode.Custom))
                ShowToast(Loc.T("Fan hızı uygulanamadı."), false);
        };
        return timer;
    }

    private bool? _lastServiceTelemetryAvailable;
    private static string ServiceUnavailableText => Loc.T("-- (Servis yok)");

    /// <summary>Tells the user once when AASSvc telemetry is lost and once when it comes back.</summary>
    private int _consecutiveTelemetryFailures;

    private void UpdateServiceTelemetryState(bool serviceAvailable)
    {
        // Ignore a single timeout/reconnect; report loss only after 3 consecutive failed polls.
        if (!serviceAvailable && ++_consecutiveTelemetryFailures < 3) return;
        if (serviceAvailable) _consecutiveTelemetryFailures = 0;

        var previous = _lastServiceTelemetryAvailable;
        _lastServiceTelemetryAvailable = serviceAvailable;
        if (previous == serviceAvailable) return;

        if (!serviceAvailable)
        {
            ShowToast(Loc.T("Acer servisine (AcerAgentService) ulaşılamıyor; yalnızca Windows ve NVIDIA'dan okunabilen veriler gösteriliyor."), false);
        }
        else if (previous == false)
        {
            ShowToast(Loc.T("Acer servisi bağlantısı geri geldi; tüm sensörler okunuyor."));
        }
    }

    private void ApplyServiceUnavailableTelemetryText(MonitorData data)
    {
        TxtSideCpuTemp.Text = ServiceUnavailableText;
        TxtSideCpuFan.Text = ServiceUnavailableText;
        TxtMonCpuTemp.Text = ServiceUnavailableText;
        TxtCpuFanRpm.Text = ServiceUnavailableText;
        TxtGpuFanRpm.Text = ServiceUnavailableText;
        TxtMonSysTemp.Text = ServiceUnavailableText;
        TxtMonCpuFreqGhz.Text = ServiceUnavailableText;
        if (TxtSubMoboTemp != null) TxtSubMoboTemp.Text = ServiceUnavailableText;
        if (data.GpuTemperature <= 0)
        {
            // NVML did not report either; do not claim the GPU is asleep.
            TxtSideGpuTemp.Text = ServiceUnavailableText;
            TxtMonGpuTemp.Text = ServiceUnavailableText;
        }
        TxtMonGpuUsage.Text = ServiceUnavailableText;
    }

    private async Task PollTelemetryAsync()
    {
        if (_isPolling) return;
        _isPolling = true;

        try
        {
            var data = await _agentClient.GetMonitorDataAsync();

            // AASSvc unavailable: fall back to what Windows/NVML can measure (CPU load, RAM, GPU via NVML).
            // Sensors only the Acer service provides (CPU temperature, fans, board temperature) are shown as
            // unavailable, and the sample is kept out of thermal history, the chart and the OSD.
            bool isFallback = data == null;
            if (isFallback)
            {
                data = _hardwareService.CreateFallbackMonitorData();
            }
            UpdateServiceTelemetryState(serviceAvailable: !isFallback);

        if (data != null)
        {
            if (!isFallback)
            {
                // Enrich with GPU Wattage, VRAM and CPU Package Power
                // GPU1_TEMPERATURE 0 from AASSvc means the dGPU is powered down; do not wake it via NVML.
                _hardwareService.EnrichMonitorData(data, gpuAsleepPerService: data.GpuTemperature <= 0);
            }
            _telemetrySampleIndex++;

            bool isWindowVisible = IsVisible && WindowState != WindowState.Minimized;
            bool isGpuSleeping = data.GpuTemperature <= 0;

            if (isWindowVisible)
            {
                // Titlebar / Sidebar
                TxtSideCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
                TxtSideGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping, data.GpuMetricsAvailable);
                TxtSideCpuFan.Text = TelemetryFormatter.FormatFanSpeedRpm(data.CpuFanSpeed);

                // Sub-Component Thermal Matrix: Motherboard / VRM (SYS1_TEMPERATURE)
                if (TxtSubMoboTemp != null)
                    TxtSubMoboTemp.Text = TelemetryFormatter.FormatTemperature(data.SysTemperature > 0 ? data.SysTemperature : null);

                // Tab 1: Fans
                TxtCpuFanRpm.Text = TelemetryFormatter.FormatFanSpeedRpm(data.CpuFanSpeed);
                TxtGpuFanRpm.Text = TelemetryFormatter.FormatFanSpeedRpm(data.GpuFanSpeed);
                BarCpuFan.Value = data.CpuFanSpeed;
                BarGpuFan.Value = data.GpuFanSpeed;

                // Tab 2: Monitoring - CPU
                var coreText = (_hardwareInfo?.FormatCoreText(data.CpuCores)) ?? (data.CpuCores > 0 ? Loc.T($"{data.CpuCores} iş parçacığı") : "");
                TxtCpuCoreCount.Text = string.IsNullOrEmpty(coreText) ? "" : $" ({coreText})";
                TxtMonCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
                BarMonCpuTemp.Value = data.CpuTemperature;
                ApplyTempSeverity(BarMonCpuTemp, TxtMonCpuTemp, data.CpuTemperature, data.CpuTemperature > 0);

                TxtMonCpuPower.Text = TelemetryFormatter.FormatCpuPower(data.CpuPowerWatts) + Loc.T(" (tahmini)");
                SetBarValueGrowing(BarMonCpuPower, data.CpuPowerWatts, 10);

                TxtMonCpuUsage.Text = FormattableString.Invariant($"{data.CpuUsage:F1} %");
                BarMonCpuUsage.Value = data.CpuUsage;

                TxtMonCpuFreqGhz.Text = TelemetryFormatter.FormatCpuFrequency(data.CpuFrequencyMHz);
                SetBarValueGrowing(BarMonCpuFreq, data.CpuFrequencyMHz, 100);

                // Tab 2: Monitoring - GPU
                TxtMonGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping, data.GpuMetricsAvailable);
                BarMonGpuTemp.Value = data.GpuTemperature;
                ApplyTempSeverity(BarMonGpuTemp, TxtMonGpuTemp, data.GpuTemperature, data.GpuMetricsAvailable && !isGpuSleeping);

                TxtMonGpuPower.Text = TelemetryFormatter.FormatGpuPower(data.GpuPowerWatts, isGpuSleeping, data.GpuMetricsAvailable);
                SetBarValueGrowing(BarMonGpuPower, data.GpuPowerWatts, 10);

                TxtMonGpuUsage.Text = FormattableString.Invariant($"{data.GpuUsage:F1} %");
                BarMonGpuUsage.Value = data.GpuUsage;

                BarMonVram.Maximum = data.VramTotalMB > 0 ? data.VramTotalMB : 8192;
                BarMonVram.Value = Math.Min(data.VramUsedMB, BarMonVram.Maximum);
                TxtMonVram.Text = TelemetryFormatter.FormatVramUsage(data.VramUsedMB, data.VramTotalMB, isGpuSleeping, data.GpuMetricsAvailable);

                // RAM
                if (data.RamTotalMB > 0)
                {
                    var ramUsedGB = (data.RamTotalMB * (data.RamUsage / 100.0)) / 1024.0;
                    var ramTotalGB = data.RamTotalMB / 1024.0;
                    TxtMonRamDetail.Text = FormattableString.Invariant($"{ramUsedGB:F1} GB / {ramTotalGB:F1} GB");
                    TxtMonRamPercent.Text = FormattableString.Invariant($"{data.RamUsage:F1} %");
                    BarMonRam.Value = data.RamUsage;
                }
                else
                {
                    TxtMonRamDetail.Text = Loc.T("-- GB / -- GB");
                    TxtMonRamPercent.Text = "-- %";
                    BarMonRam.Value = 0;
                }

                TxtMonSysTemp.Text = TelemetryFormatter.FormatTemperature(data.SysTemperature > 0 ? data.SysTemperature : null);

                if (isFallback)
                {
                    ApplyServiceUnavailableTelemetryText(data);
                }
            }

            // Update tray tooltip regardless of window visibility
            if (_notifyIcon != null)
            {
                var tip = isFallback
                    ? Loc.T($"NitroLite: Acer servisi yanıt vermiyor | CPU: %{data.CpuUsage:F0}")
                    : Loc.T($"NitroLite: {_currentMode} | CPU: {data.CpuTemperature}°C | GPU: {data.GpuTemperature}°C");
                _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
            }

            _ = RetryPendingDesktopMouseProfileIfDueAsync(Environment.TickCount64);
            _ = RetryPendingGameMouseProfileAsync(Environment.TickCount64);
            // Retry a desktop restore that failed or was interrupted, even without a crash record: the pending
            // flags are the source of truth. The AC restore has its own retry below.
            bool pendingGameRestore = _interruptedGameRecovery != null || _hasPendingDesktopRestore ||
                                      _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore || _pendingMouseRestore;
            if (!_isInitializingUi && pendingGameRestore && Environment.TickCount64 >= _nextGameRecoveryAttempt)
            {
                _nextGameRecoveryAttempt = Environment.TickCount64 + 10000;
                _ = RetryPendingDesktopRestoreAsync();
            }
            // A failed Eco step (mode/boost/fan) or a lowered panel rate is retried here: nothing else calls
            // EvaluatePowerStateAsync until the next power event.
            if (!_isInitializingUi && _powerAutomationService is { } automation &&
                (automation.HasPendingPanelRestore || automation.HasPendingDcApply) &&
                Environment.TickCount64 >= _nextPanelRestoreAttempt)
            {
                _nextPanelRestoreAttempt = Environment.TickCount64 + 15000;
                _ = automation.EvaluatePowerStateAsync();
            }

            if (!isFallback)
            {
                // Update OSD if open
                if (_isOsdOpen && _osdWindow != null && _osdWindow.IsVisible)
                {
                    _osdWindow.UpdateData(data, _currentMode);
                }

                // Keep the last 60 seconds by time (not by count: polling is 3 s while hidden, 1 s while shown).
                data.Timestamp = DateTime.Now;
                _telemetryHistory.Add(data);
                var historyCutoff = data.Timestamp - ChartWindow;
                while (_telemetryHistory.Count > 0 && (_telemetryHistory[0].Timestamp < historyCutoff || _telemetryHistory.Count > 240))
                {
                    _telemetryHistory.RemoveAt(0);
                }

                RenderTelemetryChart();
                RenderModeTrace();

                // Live standby/free sizes in the RAM card while it is on screen (one cheap syscall).
                if (IsVisible && TabMonitoring?.Visibility == Visibility.Visible && !_ramCleanBusy) UpdateMemoryListText();
            }

            // Auto-sync game dropdown to running active game if tab is visible
            var activeGame = _gameService.ActiveGameProfile;
            if (activeGame != null && !_isUpdatingGameUi && CmbConfiguredGames != null && TabModes?.Visibility == Visibility.Visible)
            {
                var selectedExe = GetSelectedGameExeName();
                bool needsSync = selectedExe == null ||
                                 !selectedExe.Equals(activeGame.ExeName, StringComparison.OrdinalIgnoreCase);

                if (needsSync)
                {
                    if (_configuredGameExeNames.Any(e => e.Equals(activeGame.ExeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _isUpdatingGameUi = true;
                        try { SelectConfiguredGame(activeGame.ExeName); }
                        finally { _isUpdatingGameUi = false; }
                        UpdateSelectedGameDetails();
                    }
                    else if (_gameService.GetProfile(activeGame.ExeName, activeGame.InstallPath) != null)
                    {
                        // The active game is not in the displayed snapshot (list changed underneath); rebuild it.
                        PopulateConfiguredGames(activeGame.ExeName);
                    }
                }
            }

            // Re-read operating mode, fan mode and CPU boost so changes made elsewhere (NitroSense, the mode key,
            // Windows power settings) are reflected instead of showing the last value NitroLite set.
            if (!isFallback && (DateTime.UtcNow - _lastDeviceStateSyncUtc) >= DeviceStateSyncInterval)
            {
                _lastDeviceStateSyncUtc = DateTime.UtcNow;
                await SyncDeviceStateAsync();
            }

        }
        }
        finally
        {
            _isPolling = false;
        }
    }

    // ==========================================
    // Telemetry History Graph & Live Analytics
    // ==========================================

    private void CmbChartMetric_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || CmbChartMetric.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        _selectedChartMetric = tag;
        _appSettings.SelectedChartMetric = tag;
        SaveSettings();
        RenderTelemetryChart();
    }

    private static readonly TimeSpan ChartWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Value of <paramref name="metric"/> in sample <paramref name="d"/>, or null when the sample has no real reading
    /// for it (sensor 0, or the dGPU asleep for GPU metrics), so gaps do not drag min/average down.
    /// </summary>
    internal static double? GetChartValue(string metric, MonitorData d)
    {
        bool gpuAwake = d.GpuTemperature > 0;
        double? v = metric switch
        {
            "CPU_TEMP" => d.CpuTemperature > 0 ? d.CpuTemperature : null,
            "GPU_TEMP" => gpuAwake ? d.GpuTemperature : null,
            "CPU_WATT" => d.CpuPowerWatts > 0 ? d.CpuPowerWatts : null,
            "GPU_WATT" => gpuAwake ? d.GpuPowerWatts : null,
            "CPU_GHZ" => d.CpuFrequencyMHz > 0 ? d.CpuFrequencyMHz / 1000.0 : null,
            "CPU_USAGE" => d.CpuUsage,
            "GPU_USAGE" => gpuAwake ? d.GpuUsage : null,
            "RAM_USAGE" => d.RamTotalMB > 0 ? d.RamUsage : null,
            "VRAM_GB" => gpuAwake && d.VramTotalMB > 0 ? d.VramUsedMB / 1024.0 : null,
            _ => d.CpuTemperature > 0 ? d.CpuTemperature : null
        };
        return v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) ? v : null;
    }

    private void ClearTelemetryChart(string unit)
    {
        string dash = string.IsNullOrEmpty(unit) ? "--" : $"-- {unit}";
        TxtChartCurrentVal.Text = dash;
        TxtChartMinVal.Text = dash;
        TxtChartMaxVal.Text = dash;
        TxtChartAvgVal.Text = dash;
        ChartLine.Points = new PointCollection();
        ChartArea.Points = new PointCollection();
        if (ChartDot != null) ChartDot.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Draws the recording strip on the modes screen: the last 60 seconds of CPU temperature as a
    /// continuous ink trace, a vermilion "now" edge, and a tick wherever the mode changed. The strip
    /// is the surface's signature move, so it reads the same history the chart uses instead of
    /// running a second sampler.
    /// </summary>
    private void RenderModeTrace()
    {
        if (CanvasModeTrace == null || TraceLine == null) return;
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (TabModes == null || TabModes.Visibility != Visibility.Visible) return;

        double width = CanvasModeTrace.ActualWidth;
        double height = CanvasModeTrace.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var now = DateTime.Now;
        // The strip shows everything recorded so far, up to the 60 s window: a fresh recording fills
        // the paper instead of hugging the right edge, and the span is written out so the scale is
        // never guessed.
        var span = ChartWindow;
        if (_telemetryHistory.Count > 0)
        {
            var recorded = now - _telemetryHistory[0].Timestamp;
            if (recorded < span) span = recorded;
        }
        if (span < TimeSpan.FromSeconds(5)) span = TimeSpan.FromSeconds(5);
        var windowStart = now - span;
        var samples = new List<(double X, double Value)>();
        foreach (var d in _telemetryHistory)
        {
            if (d.Timestamp < windowStart) continue;
            var v = GetChartValue("CPU_TEMP", d);
            if (!v.HasValue) continue;
            double x = (d.Timestamp - windowStart).TotalSeconds / span.TotalSeconds * width;
            samples.Add((x, v.Value));
        }

        if (TxtTraceSpan != null) TxtTraceSpan.Text = Loc.T($"KAYIT · {span.TotalSeconds:0} SANİYE");

        if (samples.Count < 2)
        {
            TraceLine.Points = new PointCollection();
            if (TxtTraceValue != null) TxtTraceValue.Text = "-- °C";
            if (TxtTraceRange != null) TxtTraceRange.Text = "--";
            if (TxtTraceEmpty != null) TxtTraceEmpty.Visibility = Visibility.Visible;
            HideTraceMarks(0);
            return;
        }

        if (TxtTraceEmpty != null) TxtTraceEmpty.Visibility = Visibility.Collapsed;

        double low = samples[0].Value, high = samples[0].Value;
        foreach (var s in samples)
        {
            if (s.Value < low) low = s.Value;
            if (s.Value > high) high = s.Value;
        }
        // A flat trace must not sit on the floor: hold a 4 °C band around the value.
        if (high - low < 4)
        {
            double mid = (high + low) / 2;
            low = mid - 2;
            high = mid + 2;
        }

        const double pad = 7;
        double usable = Math.Max(1, height - pad * 2);
        var points = new PointCollection(samples.Count);
        foreach (var s in samples)
        {
            double y = pad + (1 - (s.Value - low) / (high - low)) * usable;
            points.Add(new WpfPoint(s.X, y));
        }
        TraceLine.Points = points;

        if (TraceNow != null)
        {
            TraceNow.X1 = width - 0.5;
            TraceNow.X2 = width - 0.5;
            TraceNow.Y1 = 0;
            TraceNow.Y2 = height;
        }

        if (TxtTraceValue != null) TxtTraceValue.Text = $"{samples[samples.Count - 1].Value:0} °C";
        if (TxtTraceRange != null) TxtTraceRange.Text = $"{low:0}–{high:0} °C";

        DrawTraceMarks(windowStart, span, width, height);
    }

    private void DrawTraceMarks(DateTime windowStart, TimeSpan span, double width, double height)
    {
        if (TraceMarks == null) return;

        var ink = TryFindResource("Vermilion") as SolidColorBrush ?? System.Windows.Media.Brushes.Red;
        while (_traceMarkLines.Count < MaxTraceMarks)
        {
            var line = new System.Windows.Shapes.Line
            {
                Stroke = ink,
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
            };
            TraceMarks.Children.Add(line);
            _traceMarkLines.Add(line);
        }

        int used = 0;
        foreach (var mark in _modeMarks)
        {
            if (mark.At < windowStart || used >= MaxTraceMarks) continue;
            double x = (mark.At - windowStart).TotalSeconds / span.TotalSeconds * width;
            var line = _traceMarkLines[used++];
            line.X1 = x;
            line.X2 = x;
            line.Y1 = 0;
            line.Y2 = height;
            line.Visibility = Visibility.Visible;
        }
        HideTraceMarks(used);
    }

    private void HideTraceMarks(int from)
    {
        for (int i = from; i < _traceMarkLines.Count; i++)
        {
            _traceMarkLines[i].Visibility = Visibility.Collapsed;
        }
    }

    private void RenderTelemetryChart()
    {
        if (CanvasHistoryChart == null) return;
        if (!IsVisible || WindowState == WindowState.Minimized || TabMonitoring?.Visibility != Visibility.Visible) return;

        double width = CanvasHistoryChart.ActualWidth;
        double height = CanvasHistoryChart.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            width = 720;
            height = 140;
        }

        // Time-based window: x follows the sample time, so the chart really spans the last 60 seconds whatever the
        // polling rate was (3 s while hidden, 1 s while shown).
        var now = DateTime.Now;
        var windowStart = now - ChartWindow;
        var samples = new List<(DateTime Time, double Value)>();
        foreach (var d in _telemetryHistory)
        {
            if (d.Timestamp < windowStart) continue;
            var v = GetChartValue(_selectedChartMetric, d);
            if (v.HasValue) samples.Add((d.Timestamp, v.Value));
        }
        List<double> values = samples.Select(s => s.Value).ToList();

        string unit = _selectedChartMetric switch
        {
            "CPU_TEMP" or "GPU_TEMP" => "°C",
            "CPU_WATT" or "GPU_WATT" => "W",
            "CPU_GHZ" => "GHz",
            "CPU_USAGE" or "GPU_USAGE" or "RAM_USAGE" => "%",
            "VRAM_GB" => "GB",
            _ => ""
        };

        if (values.Count < 2)
        {
            ClearTelemetryChart(unit);
            return;
        }

        double current = values.Last();
        double min = values.Min();
        double max = values.Max();
        double avg = values.Average();

        bool isFine = _selectedChartMetric is "CPU_GHZ" or "VRAM_GB";
        TxtChartCurrentVal.Text = isFine ? FormattableString.Invariant($"{current:F2} {unit}") : FormattableString.Invariant($"{current:F1} {unit}");
        TxtChartMinVal.Text = isFine ? FormattableString.Invariant($"{min:F2} {unit}") : FormattableString.Invariant($"{min:F1} {unit}");
        TxtChartMaxVal.Text = isFine ? FormattableString.Invariant($"{max:F2} {unit}") : FormattableString.Invariant($"{max:F1} {unit}");
        TxtChartAvgVal.Text = isFine ? FormattableString.Invariant($"{avg:F2} {unit}") : FormattableString.Invariant($"{avg:F1} {unit}");

        double rangeMin;
        double rangeMax;

        switch (_selectedChartMetric)
        {
            case "CPU_GHZ":
                // Dynamic adaptive scaling for CPU clock: auto-scale so 100 MHz variations show visible peaks & valleys
                double ghzMin = Math.Floor(min * 2.0) / 2.0 - 0.25;
                double ghzMax = Math.Ceiling(max * 2.0) / 2.0 + 0.25;
                if (ghzMax - ghzMin < 0.8) { ghzMin = Math.Max(0.5, ghzMin - 0.4); ghzMax = ghzMin + 0.8; }
                rangeMin = Math.Max(0.0, ghzMin);
                rangeMax = ghzMax;
                break;

            case "CPU_TEMP":
            case "GPU_TEMP":
                // Adaptive scale for temperature: 5°C padding so thermal curves are prominent
                double tempMin = Math.Floor(min / 5.0) * 5.0 - 5.0;
                double tempMax = Math.Ceiling(max / 5.0) * 5.0 + 5.0;
                if (tempMax - tempMin < 15.0) { tempMin = Math.Max(25.0, tempMin - 5.0); tempMax = tempMin + 15.0; }
                rangeMin = Math.Max(20.0, tempMin);
                rangeMax = Math.Min(115.0, tempMax);
                break;

            case "CPU_WATT":
            case "GPU_WATT":
                double wattMin = Math.Max(0.0, Math.Floor(min / 10.0) * 10.0 - 5.0);
                double wattMax = Math.Ceiling(max / 10.0) * 10.0 + 10.0;
                if (wattMax - wattMin < 25.0) wattMax = wattMin + 25.0;
                rangeMin = wattMin;
                rangeMax = wattMax;
                break;

            case "CPU_USAGE":
            case "GPU_USAGE":
            case "RAM_USAGE":
                if (max < 40.0)
                {
                    rangeMin = 0.0;
                    rangeMax = 50.0;
                }
                else
                {
                    rangeMin = 0.0;
                    rangeMax = 100.0;
                }
                break;

            case "VRAM_GB":
                rangeMin = 0.0;
                rangeMax = Math.Max(8.0, Math.Ceiling(max + 1.0));
                break;

            default:
                rangeMin = Math.Max(0.0, min - 5.0);
                rangeMax = max + 5.0;
                break;
        }

        // Update grid reference labels
        if (TxtChartGridTop != null)
            TxtChartGridTop.Text = isFine ? FormattableString.Invariant($"{rangeMax:F1} {unit}") : $"{(int)rangeMax} {unit}";
        if (TxtChartGridMid != null)
            TxtChartGridMid.Text = isFine ? FormattableString.Invariant($"{((rangeMin + rangeMax) / 2.0):F1} {unit}") : $"{(int)((rangeMin + rangeMax) / 2.0)} {unit}";
        if (TxtChartGridBottom != null)
            TxtChartGridBottom.Text = isFine ? FormattableString.Invariant($"{rangeMin:F1} {unit}") : $"{(int)rangeMin} {unit}";

        double range = rangeMax - rangeMin;
        if (range <= 0) range = 1;

        int count = samples.Count;
        double windowSeconds = ChartWindow.TotalSeconds;

        var linePoints = new PointCollection();
        var areaPoints = new PointCollection();

        double firstX = Math.Clamp((samples[0].Time - windowStart).TotalSeconds / windowSeconds, 0.0, 1.0) * width;
        areaPoints.Add(new WpfPoint(firstX, height));

        double lastX = 0;
        double lastY = height / 2;

        for (int i = 0; i < count; i++)
        {
            double x = Math.Clamp((samples[i].Time - windowStart).TotalSeconds / windowSeconds, 0.0, 1.0) * width;
            double normalized = Math.Clamp((samples[i].Value - rangeMin) / range, 0.0, 1.0);
            double y = height - (normalized * (height - 24)) - 12;

            var pt = new WpfPoint(x, y);
            linePoints.Add(pt);
            areaPoints.Add(pt);

            lastX = x;
            lastY = y;
        }

        areaPoints.Add(new WpfPoint(lastX, height));

        ChartLine.Points = linePoints;
        ChartArea.Points = areaPoints;

        if (ChartDot != null)
        {
            Canvas.SetLeft(ChartDot, lastX - 3.5);
            Canvas.SetTop(ChartDot, lastY - 3.5);
            ChartDot.Visibility = Visibility.Visible;
        }
    }

    // ==========================================
    // On-Screen Display (OSD Overlay)
    // ==========================================

    private void BtnToggleOsd_Click(object sender, RoutedEventArgs e)
    {
        ToggleOsd(!_isOsdOpen);
    }

    private void BtnCycleOsdCorner_Click(object sender, RoutedEventArgs e)
    {
        if (_osdWindow == null)
        {
            _osdWindow = new OsdOverlayWindow();
            _osdWindow.SetCorner(_appSettings.OsdCorner);
            _osdWindow.SetMonitor(_appSettings.OsdMonitor);
        }
        _osdWindow.CycleCorner();
        _appSettings.OsdCorner = _osdWindow.CurrentCorner;
        _appSettings.OsdMonitor = _osdWindow.CurrentMonitor;
        SaveSettings();

        string cornerDesc = _osdWindow.CurrentCorner switch
        {
            0 => Loc.T("Sol Üst"),
            1 => Loc.T("Sağ Üst"),
            2 => Loc.T("Sağ Alt"),
            3 => Loc.T("Sol Alt"),
            _ => Loc.T("Sol Üst")
        };
        if (OsdOverlayWindow.MonitorCount > 1)
        {
            cornerDesc += Loc.T($", monitör {_osdWindow.CurrentMonitor + 1}/{OsdOverlayWindow.MonitorCount}");
        }
        ShowToast(_isOsdOpen
            ? Loc.T($"OSD konumu: {cornerDesc}")
            : Loc.T($"OSD kapalı; konum kaydedildi ({cornerDesc}). Açınca orada görünecek."));
    }

    private void ToggleOsd(bool enable, bool showToast = true)
    {
        _isOsdOpen = enable;
        _appSettings.OsdEnabled = enable;
        if (!_isInitializingUi)
        {
            SaveSettings();
        }

        if (_isOsdOpen)
        {
            if (_osdWindow == null)
            {
                _osdWindow = new OsdOverlayWindow();
            }
            _osdWindow.SetCorner(_appSettings.OsdCorner);
            _osdWindow.SetMonitor(_appSettings.OsdMonitor);
            _osdWindow.Show();
            UpdateTelemetryPollingRate();
            BtnToggleOsd.Background = (SolidColorBrush)FindResource("AccentCyan");
            BtnToggleOsd.Foreground = TryFindResource("OnAccent") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
            BtnToggleOsd.Content = Loc.T("OSD Katmanı: Açık (Kapat)");

            if (showToast) ShowToast(Loc.T("Ekran üstü gösterge etkinleştirildi."));
        }
        else
        {
            _osdWindow?.Hide();
            UpdateTelemetryPollingRate();
            BtnToggleOsd.Background = System.Windows.Media.Brushes.Transparent;
            BtnToggleOsd.Foreground = (SolidColorBrush)FindResource("AccentCyan");
            BtnToggleOsd.Content = Loc.T("Ekran Üstü Gösterge (OSD)");
            if (showToast) ShowToast(Loc.T("Ekran üstü gösterge kapatıldı."));
        }
    }

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

    public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode)
    {
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
            System.Diagnostics.Debug.WriteLine($"[GPU Mode] Refresh error: {ex.Message}");
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
    }

    private Task<AtkMouseResult> ApplyMouseProfileOnWorkerAsync(int targetProfile)
    {
        var switcher = MouseProfileSwitcher;
        return Task.Run(() => switcher == null
            ? _atkMouseService.ApplyProfile(targetProfile)
            : switcher(targetProfile) ? AtkMouseResult.Confirmed : AtkMouseResult.NoResponse);
    }

    private async Task<bool> SwitchMouseProfileInternalAsync(int targetProfile) =>
        await ApplyMouseProfileOnWorkerAsync(targetProfile) == AtkMouseResult.Confirmed;

    private async void BtnTestMouseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbGameAtkMouseProfile == null) return;
        var selectedIdx = CmbGameAtkMouseProfile.SelectedIndex;
        if (selectedIdx <= 0)
        {
            ShowToast(Loc.T("Test için 1-4 arasında bir ATK fare profili seçin."), false);
            return;
        }

        if (BtnTestMouseProfile != null) BtnTestMouseProfile.IsEnabled = false;
        var previousRequest = _pendingDesktopMouseRequest;
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (!_appSettings.AtkMouseIntegrationEnabled) return;
            var result = await ApplyMouseProfileOnWorkerAsync(selectedIdx);
            if (result == AtkMouseResult.Confirmed)
            {
                if (ReferenceEquals(_pendingDesktopMouseRequest, previousRequest))
                    _pendingDesktopMouseRequest = null;
                if (!_isGameSessionActive)
                {
                    _savedMouseProfileBeforeGame = selectedIdx;
                }
                UpdateSideMouseProfileStatus(selectedIdx);
                ShowToast(Loc.T($"ATK A9 Fare donanımı Config {selectedIdx} profiline geçirildi."));
            }
            else
            {
                ShowToast(DescribeMouseFailure(result), false);
            }
        }
        finally
        {
            _gameProfileTransitionLock.Release();
            if (BtnTestMouseProfile != null) BtnTestMouseProfile.IsEnabled = true;
        }
    }

    private void InitializeDefaultMouseProfileUi()
    {
        if (CmbDefaultAtkMouseProfile == null) return;
        int profile = _appSettings.DefaultAtkMouseProfile; // 0..4, default is 1
        int selectIdx = profile switch
        {
            1 => 0, // Config 1
            2 => 1, // Config 2
            3 => 2, // Config 3
            4 => 3, // Config 4
            _ => 4  // Son Kullanılanı Koru (0)
        };
        CmbDefaultAtkMouseProfile.SelectedIndex = selectIdx;

        if (!_appSettings.AtkMouseIntegrationEnabled)
        {
            UpdateDefaultMouseProfileBadge(profile);
            return;
        }

        // Nothing is shown as active until the mouse itself confirms it.
        UpdateSideMouseProfileStatus(0);
        _ = ApplyDesktopMouseProfileAsync(profile);
    }

    private static readonly TimeSpan MouseRetryInterval = TimeSpan.FromSeconds(5);

    private sealed class DesktopMouseRequest(int profile)
    {
        public int Profile { get; } = profile;
        public long NextAttemptAt { get; set; } = long.MaxValue;
    }

    private DesktopMouseRequest? _pendingDesktopMouseRequest;

    internal Task<AtkMouseResult?> ApplyDesktopMouseProfileAsync(int profile)
    {
        var request = new DesktopMouseRequest(profile);
        _pendingDesktopMouseRequest = request;
        UpdateDefaultMouseProfileBadge(profile, confirmed: false);
        return TryApplyDesktopMouseProfileAsync(request);
    }

    internal async Task RetryPendingDesktopMouseProfileIfDueAsync(long now)
    {
        if (_pendingDesktopMouseRequest is not { } request || now < request.NextAttemptAt ||
            _isGameSessionActive || !_appSettings.AtkMouseIntegrationEnabled) return;

        // Only one attempt for this request may wait on the transition lock at a time.
        request.NextAttemptAt = long.MaxValue;
        await TryApplyDesktopMouseProfileAsync(request);
    }

    private async Task<AtkMouseResult?> TryApplyDesktopMouseProfileAsync(DesktopMouseRequest request)
    {
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            // A newer selection can replace this request while we wait for a game transition.
            if (!ReferenceEquals(_pendingDesktopMouseRequest, request) || !_appSettings.AtkMouseIntegrationEnabled)
                return null;

            int profile = request.Profile;
            if (_isGameSessionActive)
            {
                request.NextAttemptAt = Environment.TickCount64;
                UpdateDefaultMouseProfileBadge(profile, confirmed: false, status: "Oyun bitince uygulanacak");
                return null;
            }

            int? activeProfile = null;
            AtkMouseResult result;
            if (profile > 0)
            {
                result = await ApplyMouseProfileOnWorkerAsync(profile);
                if (result == AtkMouseResult.Confirmed)
                {
                    activeProfile = profile;
                    _savedMouseProfileBeforeGame = profile;
                }
            }
            else if (MouseProfileSwitcher == null)
            {
                (activeProfile, result) = await Task.Run(() =>
                {
                    int? current = _atkMouseService.QueryCurrentProfile(out var readResult);
                    return (current, readResult);
                });
            }
            else result = AtkMouseResult.Confirmed;

            if (!ReferenceEquals(_pendingDesktopMouseRequest, request) || !_appSettings.AtkMouseIntegrationEnabled)
                return null;

            if (result is AtkMouseResult.Confirmed or AtkMouseResult.Rejected)
                _pendingDesktopMouseRequest = null;
            else
                request.NextAttemptAt = Environment.TickCount64 + (long)MouseRetryInterval.TotalMilliseconds;

            UpdateDefaultMouseProfileBadge(profile,
                confirmed: result == AtkMouseResult.Confirmed,
                status: result switch
                {
                    AtkMouseResult.Confirmed => null,
                    AtkMouseResult.Rejected => "Reddedildi",
                    _ => "Uygulama bekleniyor"
                },
                tooltip: result == AtkMouseResult.Confirmed ? null : DescribeMouseFailure(result));
            UpdateSideMouseProfileStatus(activeProfile ?? 0, failure: result,
                retryPending: _pendingDesktopMouseRequest != null);
            return result;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_pendingDesktopMouseRequest, request))
                request.NextAttemptAt = Environment.TickCount64 + (long)MouseRetryInterval.TotalMilliseconds;
            App.WriteLog("ATK fare profili", ex.Message);
            return AtkMouseResult.CommunicationError;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private static string DescribeMouseFailure(AtkMouseResult result) => result switch
    {
        AtkMouseResult.ReceiverNotFound => Loc.T("ATK alıcısı bulunamadı"),
        AtkMouseResult.NoResponse => Loc.T("Fare yanıt vermedi"),
        AtkMouseResult.Rejected => Loc.T("Fare profil değişikliğini reddetti"),
        AtkMouseResult.ProfileNotConfirmed => Loc.T("Fare profili doğrulanamadı"),
        _ => Loc.T("ATK alıcısıyla iletişim kurulamadı")
    };

    /// <summary>
    /// Compact variant for the sidebar status card, where the value shares a ~154 px row with its label
    /// (about 95 px for the value itself). The full text is still used in toasts and tooltips.
    /// </summary>
    /// <param name="retryPending">
    /// True while the desktop profile is still being retried. A silent wireless mouse is retried every few
    /// seconds, so "no response" read as a dead end; the retrying state says what is actually happening.
    /// </param>
    internal static string DescribeMouseFailureShort(AtkMouseResult result, bool retryPending = false) => result switch
    {
        AtkMouseResult.ReceiverNotFound => Loc.T("Alıcı yok"),
        AtkMouseResult.NoResponse => retryPending ? Loc.T("Uyanıyor…") : Loc.T("Yanıt yok"),
        AtkMouseResult.Rejected => Loc.T("Reddedildi"),
        AtkMouseResult.ProfileNotConfirmed => Loc.T("Doğrulanamadı"),
        _ => Loc.T("İletişim hatası")
    };

    private (int Generation, int Profile, int Remaining, long Due)? _pendingGameMouseProfile;

    private async Task<AtkMouseResult> TryApplyGameMouseProfileAsync(int target, int generation)
    {
        // In keep-last mode, never change the mouse unless its desktop profile is known.
        if (_savedMouseProfileBeforeGame == 0)
        {
            if (MouseProfileSwitcher != null) return AtkMouseResult.ProfileNotConfirmed;
            var (current, result) = await Task.Run(() =>
            {
                int? profile = _atkMouseService.QueryCurrentProfile(out var status);
                return (profile, status);
            });
            if (!current.HasValue) return result;
            if (generation != _gameSessionGeneration || !_appSettings.AtkMouseIntegrationEnabled)
                return AtkMouseResult.Rejected;
            _savedMouseProfileBeforeGame = current.Value;
            if (_activeGameRecovery is { } recovery)
            {
                recovery.SavedMouseProfile = current.Value;
                SaveGameSessionRecoveryState(recovery);
            }
        }
        return await ApplyMouseProfileOnWorkerAsync(target);
    }

    private async Task RetryPendingGameMouseProfileAsync(long now)
    {
        if (_pendingGameMouseProfile is not { } request || now < request.Due) return;
        _pendingGameMouseProfile = null;
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            if (!_isGameSessionActive || !_gameService.AutoGameModeEnabled || !_appSettings.AtkMouseIntegrationEnabled ||
                request.Generation != _gameSessionGeneration) return;
            var result = await TryApplyGameMouseProfileAsync(request.Profile, request.Generation);
            if (result == AtkMouseResult.Confirmed)
            {
                _isMouseProfileSwitchedForGame = true;
                UpdateSideMouseProfileStatus(request.Profile);
            }
            else if (result != AtkMouseResult.Rejected && request.Remaining > 1)
                _pendingGameMouseProfile = (request.Generation, request.Profile, request.Remaining - 1,
                    Environment.TickCount64 + 3000);
        }
        catch (Exception ex) { App.WriteLog("Oyun fare profili", ex.Message); }
        finally { _gameProfileTransitionLock.Release(); }
    }

    private void UpdateSideMouseProfileStatus(int profile, AtkMouseResult failure = AtkMouseResult.Confirmed,
        bool retryPending = false)
    {
        if (TxtSideMouseProfile == null) return;
        if (profile >= 1 && profile <= 4)
        {
            TxtSideMouseProfile.Text = Loc.T($"Config {profile}");
            TxtSideMouseProfile.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            TxtSideMouseProfile.ToolTip = null;
        }
        else
        {
            TxtSideMouseProfile.Text = failure == AtkMouseResult.Confirmed
                ? Loc.T("Bilinmiyor")
                : DescribeMouseFailureShort(failure, retryPending);
            TxtSideMouseProfile.Foreground = (SolidColorBrush)FindResource("TextSecondary");
            TxtSideMouseProfile.ToolTip = failure == AtkMouseResult.NoResponse
                ? Loc.T("Kablosuz fare uykuda olabilir. Fareyi hareket ettirin.")
                : null;
        }
    }

    private void UpdateAtkMouseIntegrationVisibility(bool enabled)
    {
        if (CardDefaultMouseProfile != null)
        {
            CardDefaultMouseProfile.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (PnlGameAtkMouseProfile != null)
        {
            PnlGameAtkMouseProfile.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (GridGameSettingsRow != null)
        {
            GridGameSettingsRow.Columns = enabled ? 4 : 3;
        }

        if (RowSideMouseProfile != null)
        {
            RowSideMouseProfile.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (enabled)
        {
            UpdateSideMouseProfileStatus(_atkMouseService.HasConfirmedProfile ? _atkMouseService.CurrentActiveProfile : 0);
        }
    }

    private async void ChkAtkMouseIntegration_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAtkMouseIntegration == null) return;
        bool isEnabled = ChkAtkMouseIntegration.IsChecked == true;
        await SetMouseIntegrationEnabledAsync(isEnabled);
    }

    internal async Task SetMouseIntegrationEnabledAsync(bool isEnabled)
    {
        _appSettings.AtkMouseIntegrationEnabled = isEnabled;
        SaveSettings();
        UpdateAtkMouseIntegrationVisibility(isEnabled);

        if (isEnabled)
        {
            InitializeDefaultMouseProfileUi();
            ShowToast(Loc.T("ATK Fare Entegrasyonu etkinleştirildi."));
        }
        else
        {
            _pendingDesktopMouseRequest = null;
            // Acquire the lock FIRST, then check flags.
            // If a mouse command is in-flight (running under the lock), it hasn't set
            // _isMouseProfileSwitchedForGame yet — reading flags before acquiring would see false
            // and skip the restore, leaving the mouse stuck in the game profile.
            await _gameProfileTransitionLock.WaitAsync();
            try
            {
                if (_isMouseProfileSwitchedForGame || _pendingMouseRestore)
                {
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
                            ShowToast(Loc.T($"ATK Fare Entegrasyonu devre dışı bırakıldı (Fare Config {targetMouseProfile} profiline geri döndürüldü)."));
                        }
                        else
                        {
                            _pendingMouseRestore = true;
                            ShowToast(Loc.T("ATK Fare Entegrasyonu devre dışı bırakıldı (Masaüstü fare profiline geri dönülemedi)."), false);
                        }
                    }
                }
                else
                {
                    ShowToast(Loc.T("ATK Fare Entegrasyonu devre dışı bırakıldı."));
                }
            }
            finally
            {
                _gameProfileTransitionLock.Release();
            }
        }
    }

    private void UpdateDefaultMouseProfileBadge(int profile, bool confirmed = true, string? status = null, string? tooltip = null)
    {
        if (TxtDefaultMouseProfileStatus == null) return;
        TxtDefaultMouseProfileStatus.ToolTip = tooltip;
        if (profile >= 1 && profile <= 4)
        {
            string suffix = status ?? (confirmed ? Loc.T("Aktif") : Loc.T("Uygulanıyor…"));
            string brushKey = confirmed ? "AccentGreen" : "TextSecondary";
            TxtDefaultMouseProfileStatus.Text = Loc.T($"Config {profile} ({suffix})");
            TxtDefaultMouseProfileStatus.Foreground = (SolidColorBrush)FindResource(brushKey);
        }
        else
        {
            TxtDefaultMouseProfileStatus.Text = Loc.T("Son Kullanılan (Pasif)");
            TxtDefaultMouseProfileStatus.Foreground = (SolidColorBrush)FindResource("TextSecondary");
        }
    }

    private void CmbDefaultAtkMouseProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi || CmbDefaultAtkMouseProfile == null) return;
        if (CmbDefaultAtkMouseProfile.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int val))
        {
            if (val != _appSettings.DefaultAtkMouseProfile) _pendingDesktopMouseRequest = null;
            _appSettings.DefaultAtkMouseProfile = val;
            SaveSettings();
            if (val >= 1 && val <= 4)
            {
                // Saved as the default, but not sent to the mouse until "Uygula" (or the next game exit).
                UpdateDefaultMouseProfileBadge(val, confirmed: false, status: Loc.T("Uygula'ya basın"));
            }
            else
            {
                UpdateDefaultMouseProfileBadge(val);
            }
        }
    }

    private async void BtnApplyDefaultMouseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDefaultAtkMouseProfile == null) return;
        int val = 1;
        if (CmbDefaultAtkMouseProfile.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int parsed))
        {
            val = parsed;
        }

        _appSettings.DefaultAtkMouseProfile = val;
        bool saved = SaveSettings();
        string saveStatus = saved ? $"Config {val} kaydedildi" : $"Config {val} diske kaydedilemedi";

        if (val >= 1 && val <= 4)
        {
            if (BtnApplyDefaultMouseProfile != null) BtnApplyDefaultMouseProfile.IsEnabled = false;
            try
            {
                var result = await ApplyDesktopMouseProfileAsync(val);
                if (result == AtkMouseResult.Confirmed)
                {
                    ShowToast(Loc.T($"{saveStatus}. Fareye uygulandı."), saved);
                }
                else if (result is AtkMouseResult.Rejected)
                {
                    ShowToast($"{saveStatus}. {DescribeMouseFailure(result.Value)}.", false);
                }
                else if (result.HasValue)
                {
                    ShowToast(Loc.T($"{saveStatus}. {DescribeMouseFailure(result.Value)}. Tekrar denenecek."), false);
                }
                else if (_pendingDesktopMouseRequest?.Profile == val && _isGameSessionActive)
                {
                    ShowToast(Loc.T($"{saveStatus}. Oyun bitince uygulanacak."), saved);
                }
            }
            finally
            {
                if (BtnApplyDefaultMouseProfile != null) BtnApplyDefaultMouseProfile.IsEnabled = true;
            }
        }
        else
        {
            _pendingDesktopMouseRequest = null;
            UpdateDefaultMouseProfileBadge(val);
            if (saved) ShowToast(Loc.T("Varsayılan fare profili otomatik koruma (pasif) olarak kaydedildi."));
        }
    }

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
                TargetAtkMouseProfile = stored.TargetAtkMouseProfile
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

            bool fanOk = await _agentClient.SetFanModeAsync(profile.TargetFanMode);
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;
            if (fanOk)
            {
                _currentFanMode = profile.TargetFanMode;
            }

            bool mouseOk = true;
            if (_appSettings.AtkMouseIntegrationEnabled && profile.TargetAtkMouseProfile > 0)
            {
                var mouseResult = await TryApplyGameMouseProfileAsync(profile.TargetAtkMouseProfile, saveGen);
                mouseOk = mouseResult == AtkMouseResult.Confirmed;
                if (!mouseOk && mouseResult != AtkMouseResult.Rejected)
                    _pendingGameMouseProfile = (saveGen, profile.TargetAtkMouseProfile, 3, Environment.TickCount64 + 3000);
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
            System.Diagnostics.Trace.WriteLine($"OnGameStarted failed: {ex.Message}");
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
                                    _pendingGameMouseProfile = (sessionGen, profile.TargetAtkMouseProfile, 3, Environment.TickCount64 + 3000);
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

                        bool fanOk = await _agentClient.SetFanModeAsync(profile.TargetFanMode);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                        if (fanOk)
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
        catch { }
    }

    private async void OnGameEnded(GameProfile profile)
    {
        try
        {
            await HandleGameEndedAsync(profile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"OnGameEnded failed: {ex.Message}");
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
        catch { }
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

    // ==========================================
    // RGB Preset Manager (Module 4) & Persistence
    // ==========================================

    private void PopulateRgbPresets(string? selectPresetName = null)
    {
        if (CmbRgbPresets == null) return;

        var presets = _rgbProfileService.GetPresets();
        CmbRgbPresets.ItemsSource = presets.Select(p => p.Name).ToList();

        string? target = selectPresetName;
        if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(_appSettings.SelectedRgbPreset))
        {
            target = _appSettings.SelectedRgbPreset;
        }

        if (!string.IsNullOrEmpty(target) && presets.Any(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            var matched = presets.First(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).Name;
            CmbRgbPresets.SelectedItem = matched;
        }
        else if (_appSettings.IsBacklightOff)
        {
            var offPreset = presets.FirstOrDefault(p => p.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase) || p.Brightness == 0);
            if (offPreset != null)
            {
                CmbRgbPresets.SelectedItem = offPreset.Name;
            }
            else
            {
                CmbRgbPresets.SelectedItem = null;
            }
        }
        else
        {
            CmbRgbPresets.SelectedItem = null;
        }
    }

    private async void CmbRgbPresets_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || CmbRgbPresets.SelectedItem is not string selectedName) return;

        var preset = _rgbProfileService.GetPresets().FirstOrDefault(p => p.Name == selectedName);
        if (preset == null) return;

        _isApplyingPreset = true;
        CancelActiveLightingReassertion();
        try
        {
            _lighting.Effect = preset.Effect;
            _lighting.Brightness = preset.Brightness;
            _lighting.Speed = preset.Speed;
            _lighting.Direction = preset.Direction;
            _lighting.Color = preset.Color;
            _lighting.LEDs = new List<string>(preset.LEDs);

            UpdateLightingUi(_lighting);
            UpdateEffectComboBox(preset.Effect);

            bool isOff = IsLightingOff(_lighting);
            if (!await ApplyLightingToDeviceAsync(Loc.T($"'{selectedName}' profili uygulanamadı"))) return;

            _appSettings.SelectedRgbPreset = selectedName;
            _appSettings.IsBacklightOff = isOff;
            _appSettings.KeyboardLighting = _lighting;
            PersistLightingState();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma profili uygulanamadı: {ex.Message}"), false);
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    private void UpdateEffectComboBox(string effect)
    {
        for (int i = 0; i < CmbRgbEffect.Items.Count; i++)
        {
            if (CmbRgbEffect.Items[i] is ComboBoxItem item && item.Content is string text)
            {
                if (text.StartsWith(effect, StringComparison.OrdinalIgnoreCase))
                {
                    CmbRgbEffect.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void BtnSaveCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtCustomPresetName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Profil {DateTime.Now:HH:mm}";
        }

        try
        {
            _rgbProfileService.SaveCustomPreset(name, _lighting.Effect, _lighting.Brightness, _lighting.Speed, _lighting.Direction, _lighting.Color, _lighting.LEDs);
        }
        catch (ArgumentException ex)
        {
            ShowToast(ex.Message, false);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Loc.T($"Aydınlatma profili kaydedilemedi: {ex.Message}"), false);
            return;
        }

        _appSettings.SelectedRgbPreset = name;
        _appSettings.KeyboardLighting = _lighting;
        _appSettings.IsBacklightOff = _lighting.Brightness == 0 || _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);
        SaveSettings();
        SaveActiveRgbState();

        PopulateRgbPresets(name);
        TxtCustomPresetName.Clear();

        ShowToast(Loc.T($"'{name}' aydınlatma profili kaydedildi."));
    }

    private void BtnDeleteCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (CmbRgbPresets.SelectedItem is not string selectedName)
        {
            ShowToast(Loc.T("Silmek için listeden bir profil seçin."), false);
            return;
        }

        var preset = _rgbProfileService.GetPresets().FirstOrDefault(p => p.Name == selectedName);
        if (preset?.IsBuiltIn == true)
        {
            ShowToast(Loc.T("Varsayılan fabrika profilleri silinemez."), false);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T($"'{selectedName}' aydınlatma profilini silmek istiyor musunuz?"),
            Loc.T("Profili Sil"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!_rgbProfileService.DeletePreset(selectedName))
        {
            ShowToast(Loc.T("Profil silinemedi."), false);
            return;
        }

        // Clear the combo first: SaveSettings() copies the combo's selection into SelectedRgbPreset,
        // which used to write the just-deleted name back.
        MarkLightingCustomized();
        SaveSettings();
        PopulateRgbPresets();
        ShowToast(Loc.T($"'{selectedName}' profili silindi."));
    }

    // ==========================================
    // Battery Health Details & Power Flow (v1.3)
    // ==========================================

    private async Task RefreshBatteryHealthAsync()
    {
        var details = await _batteryService.GetBatteryHealthDetailsAsync();
        Dispatcher.Invoke(() =>
        {
            // Values computed from a fixed factory capacity, and the hard-coded fallbacks used when Windows
            // reports nothing, are shown as estimates.
            // Only values Windows reported are shown; anything missing is "--" (no model-specific constants).
            bool measured = details.DesignCapacityMWh > 0 && details.FullChargeCapacityMWh > 0;
            bool wearEstimated = details.IsDesignCapacityEstimated || details.IsFullChargeCapacityEstimated;

            if (TxtBatHealthPercent != null)
                TxtBatHealthPercent.Text = measured ? TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.HealthPercent:F1} %"), wearEstimated) : "--";
            if (TxtBatHealthNote != null)
                TxtBatHealthNote.Text = !measured ? Loc.T("Kapasite okunamadı")
                    : details.HealthPercent >= 80 ? Loc.T("İyi")
                    : details.HealthPercent >= 60 ? Loc.T("Yıpranmış")
                    : Loc.T("Değişim önerilir");

            if (TxtBatWearPercent != null)
                TxtBatWearPercent.Text = measured ? TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.WearLevelPercent:F1} %"), wearEstimated) : "--";
            if (TxtBatWearNote != null)
                TxtBatWearNote.Text = !measured ? "--"
                    : details.WearLevelPercent < 10 ? Loc.T("Düşük aşınma (< %10)")
                    : details.WearLevelPercent < 25 ? Loc.T("Orta aşınma (%10–25)")
                    : Loc.T("Yüksek aşınma (> %25)");

            if (TxtBatFullChargeCap != null)
                TxtBatFullChargeCap.Text = details.FullChargeCapacityMWh > 0
                    ? TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.FullChargeCapacityMWh:F0} mWh"), details.IsFullChargeCapacityEstimated)
                    : "--";

            if (TxtBatDesignCap != null)
                TxtBatDesignCap.Text = details.DesignCapacityMWh > 0
                    ? Loc.T("Fabrika: ") + TelemetryFormatter.MarkEstimated(FormattableString.Invariant($"{details.DesignCapacityMWh:F0} mWh"), details.IsDesignCapacityEstimated)
                    : Loc.T("Fabrika: --");

            if (TxtBatCycleCount != null)
                TxtBatCycleCount.Text = details.CycleCount.HasValue
                    ? Loc.T($"Döngü: {details.CycleCount.Value}")
                    : Loc.T("Döngü sayısı: pil yazılımı bildirmiyor");

            if (TxtBatVoltage != null)
                TxtBatVoltage.Text = details.VoltageVolts > 0
                    ? TelemetryFormatter.MarkEstimated($"{details.VoltageVolts:F2} V", details.IsVoltageEstimated)
                    : "--";

            if (TxtBatChemistry != null)
            {
                // Li-ion cells are ~3.6-3.85 V nominal; the series cell count is inferred from the pack voltage.
                int cells = details.VoltageVolts > 0 ? (int)Math.Round(details.VoltageVolts / 3.85) : 0;
                var parts = new List<string>();
                if (details.Chemistry.Length > 0) parts.Add(details.Chemistry);
                if (cells is >= 1 and <= 8 && details.Chemistry.StartsWith("Lityum")) parts.Add(Loc.T($"~{cells} hücre (gerilimden)"));
                TxtBatChemistry.Text = parts.Count > 0 ? string.Join(" · ", parts) : "--";
            }

            if (TxtBatIdentity != null)
            {
                var id = string.Join(" ", new[] { details.Manufacturer, details.ModelName }.Where(s => !string.IsNullOrWhiteSpace(s)));
                TxtBatIdentity.Text = id.Length > 0
                    ? Loc.T($"{id} · Windows (WMI) üzerinden okunan kapasite ve gerilim.")
                    : Loc.T("Windows (WMI) üzerinden okunan kapasite ve gerilim.");
                if (TxtSubBatteryName != null)
                    TxtSubBatteryName.Text = id.Length > 0 ? Loc.T($"{id} · sıcaklık sensörü bildirilmiyor") : Loc.T("Sıcaklık sensörü bildirilmiyor");
            }
        });
    }

    private async Task RefreshStorageHealthUi()
    {
        var drives = await Task.Run(() => _storageService.GetDrives());
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;

            var d0 = drives.FirstOrDefault(d => d.DiskNumber == 0) ?? (drives.Count > 0 ? drives[0] : null);
            if (d0 != null)
            {
                if (CardDrive0 != null) CardDrive0.Visibility = Visibility.Visible;
                if (TxtDrive0Letter != null)
                {
                    string letter = !string.IsNullOrEmpty(d0.DriveLetter) ? d0.DriveLetter : "C";
                    TxtDrive0Letter.Text = letter.Contains(',') ? $"[{letter}] " : $"[{letter}:] ";
                }
                if (TxtDrive0Name != null) TxtDrive0Name.Text = $"{d0.Model}";
                if (TxtDrive0Bus != null)
                {
                    TxtDrive0Bus.Text = string.IsNullOrWhiteSpace(d0.Firmware)
                        ? $" ({d0.BusType})"
                        : $" ({d0.BusType} - {d0.Firmware})";
                }
                if (TxtDrive0Detail != null) TxtDrive0Detail.Text = TelemetryFormatter.FormatDiskCapacity(d0.UsedCapacityGB, d0.TotalCapacityGB);
                if (TxtDrive0Free != null) TxtDrive0Free.Text = Loc.TInv($"{d0.FreeCapacityGB:F1} GB Boş Alan");
                if (BarDrive0 != null)
                {
                    BarDrive0.Value = d0.UsedPercent;
                    BarDrive0.Foreground = (SolidColorBrush)FindResource(d0.UsedPercent > 85 ? "AccentRed" : "Ink");
                }
                if (TxtDrive0HealthBadge != null)
                {
                    TxtDrive0HealthBadge.Text = d0.EstHealthPercent >= 0
                        ? Loc.T($"Sağlık: %{d0.EstHealthPercent}")
                        : (string.IsNullOrWhiteSpace(d0.HealthDesc) ? Loc.T("Aşınma ölçülemiyor (yönetici izni gerekli)") : d0.HealthDesc);
                }
                if (TxtDrive0Temp != null)
                {
                    TxtDrive0Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d0.Temperature), d0.IsTemperatureEstimated);
                }
                if (TxtSubSsd1Temp != null)
                {
                    TxtSubSsd1Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d0.Temperature), d0.IsTemperatureEstimated);
                    if (TxtSubSsd1Label != null) TxtSubSsd1Label.Text = string.IsNullOrEmpty(d0.DriveLetter) ? Loc.T("Disk 1") : Loc.T($"Disk 1 ({d0.DriveLetter}:)");
                    if (TxtSubSsd1Name != null) TxtSubSsd1Name.Text = d0.Model;
                }
            }
            else
            {
                if (CardDrive0 != null) CardDrive0.Visibility = Visibility.Collapsed;
            }

            var d1 = drives.FirstOrDefault(d => d.DiskNumber == 1 && d != d0) ?? (drives.Count > 1 ? drives.FirstOrDefault(d => d != d0) : null);
            if (d1 != null)
            {
                if (CardDrive1 != null) CardDrive1.Visibility = Visibility.Visible;
                if (TxtDrive1Letter != null)
                {
                    string letter = !string.IsNullOrEmpty(d1.DriveLetter) ? d1.DriveLetter : "D";
                    TxtDrive1Letter.Text = letter.Contains(',') ? $"[{letter}] " : $"[{letter}:] ";
                }
                if (TxtDrive1Name != null) TxtDrive1Name.Text = $"{d1.Model}";
                if (TxtDrive1Bus != null)
                {
                    TxtDrive1Bus.Text = string.IsNullOrWhiteSpace(d1.Firmware)
                        ? $" ({d1.BusType})"
                        : $" ({d1.BusType} - {d1.Firmware})";
                }
                if (TxtDrive1Detail != null) TxtDrive1Detail.Text = TelemetryFormatter.FormatDiskCapacity(d1.UsedCapacityGB, d1.TotalCapacityGB);
                if (TxtDrive1Free != null) TxtDrive1Free.Text = Loc.TInv($"{d1.FreeCapacityGB:F1} GB Boş Alan");
                if (BarDrive1 != null)
                {
                    BarDrive1.Value = d1.UsedPercent;
                    BarDrive1.Foreground = (SolidColorBrush)FindResource(d1.UsedPercent > 85 ? "AccentRed" : "Ink");
                }
                if (TxtDrive1HealthBadge != null)
                {
                    TxtDrive1HealthBadge.Text = d1.EstHealthPercent >= 0
                        ? Loc.T($"Sağlık: %{d1.EstHealthPercent}")
                        : (string.IsNullOrWhiteSpace(d1.HealthDesc) ? Loc.T("Aşınma ölçülemiyor (yönetici izni gerekli)") : d1.HealthDesc);
                }
                if (TxtDrive1Temp != null)
                {
                    TxtDrive1Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d1.Temperature), d1.IsTemperatureEstimated);
                }
                if (TxtSubSsd2Temp != null)
                {
                    TxtSubSsd2Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d1.Temperature), d1.IsTemperatureEstimated);
                    if (TxtSubSsd2Label != null) TxtSubSsd2Label.Text = string.IsNullOrEmpty(d1.DriveLetter) ? Loc.T("Disk 2") : Loc.T($"Disk 2 ({d1.DriveLetter}:)");
                    if (TxtSubSsd2Name != null) TxtSubSsd2Name.Text = d1.Model;
                }
            }
            else
            {
                if (CardDrive1 != null) CardDrive1.Visibility = Visibility.Collapsed;
            }

            if (TxtStorageBadge != null)
            {
                bool anyProblem = drives.Any(d => d.HealthStatus == 1 || d.HealthStatus == 2);
                bool allHealthy = drives.Count > 0 && drives.All(d => d.HealthStatus == 0);
                if (anyProblem)
                {
                    TxtStorageBadge.Text = Loc.T("Sürücü Uyarısı Mevcut");
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("AccentRed");
                }
                else if (allHealthy)
                {
                    TxtStorageBadge.Text = Loc.T("Tüm Sürücüler Sağlıklı");
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                }
                else
                {
                    TxtStorageBadge.Text = Loc.T("Sürücü Sağlığı Bilinmiyor");
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("TextSecondary");
                }
            }
        });
    }

    /// <summary>
    /// Shows a warning on the RGB tab when the Acer service explicitly reports that this model has no
    /// controllable keyboard lighting. A null answer (service absent, unknown reply) changes nothing.
    /// </summary>
    private async Task RefreshLightingCapabilityAsync()
    {
        bool? supported;
        try
        {
            supported = await _agentClient.GetLightingCapabilityAsync();
        }
        catch
        {
            return;
        }

        if (supported != false) return;

        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (BannerLightingUnsupported != null) BannerLightingUnsupported.Visibility = Visibility.Visible;
            if (TxtLightingUnsupported != null)
                TxtLightingUnsupported.Text = Loc.T("Acer servisi bu modelde denetlenebilir klavye aydınlatması bildirmedi. "
                    + "Aşağıdaki denetimler etkisiz kalabilir; RGB servisini onarmayı deneyebilirsiniz.");
        });
    }

    private void OnBatteryStatusUpdated(BatteryStatusInfo status)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;

            // Live Battery Sub-Component Matrix Temperature
            if (TxtSubBatteryTemp != null)
            {
                TxtSubBatteryTemp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatDetailedTemperature(status.TemperatureCelsius), status.IsTemperatureEstimated);
            }

            UpdateHighPowerAvailabilityUi();

            // -1 means Windows could not report the level; show it as unknown instead of a flat battery.
            bool levelKnown = status.BatteryPercent >= 0;
            TxtBatteryHeader.Text = levelKnown ? $"{status.BatteryPercent}%" : "-- %";
            TxtBatLevelBig.Text = levelKnown ? $"{status.BatteryPercent}%" : "-- %";
            BarBatBig.Value = levelKnown ? status.BatteryPercent : 0;

            // Windows keeps its "charging" flag set while the 80 % limit holds the battery at 0 W, so decide by the
            // actual charge current when it was read.
            if (!status.IsPluggedIn)
                TxtBatStateDesc.Text = Loc.T("Pilde Çalışıyor (Deşarj)");
            else if (status.ChargeRateUnknown)
                TxtBatStateDesc.Text = Loc.T("Adaptöre Bağlı");
            else if (status.ChargeRateWatts > 0)
                TxtBatStateDesc.Text = Loc.T("Adaptöre Bağlı (Şarj Ediliyor)");
            else
                TxtBatStateDesc.Text = _batteryService.WatchdogEnabled && status.BatteryPercent >= 78
                    ? Loc.T("Adaptöre Bağlı (Şarj sınırında bekliyor)")
                    : Loc.T("Adaptöre Bağlı (Şarj olmuyor)");

            // Real-time power flow in Watts via TelemetryFormatter
            if (TxtBatDischargeRate != null)
            {
                TxtBatDischargeRate.Text = TelemetryFormatter.FormatBatteryPowerFlow(status);
                if (status.IsDischarging && status.DischargeRateWatts > 0)
                {
                    TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("AccentCyan");
                }
                else if (status.IsCharging && status.ChargeRateWatts > 0)
                {
                    TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                }
                else
                {
                    TxtBatDischargeRate.Foreground = (SolidColorBrush)FindResource("TextSecondary");
                }
            }

            if (TxtBatRemainingTime != null)
            {
                TxtBatRemainingTime.Text = Loc.T($"Kalan Süre: {TelemetryFormatter.FormatRemainingTime(status.EstimatedRemainingMinutes, status.IsPluggedIn, status.IsCharging)}");
            }

            if (status.IsOvercharged)
            {
                BannerOvercharge.Visibility = Visibility.Visible;
                TxtOverchargeMsg.Text = Loc.T($"Pil seviyesi %{status.BatteryPercent} seviyesine ulaştı. Adaptörü çıkararak deşarj işlemini başlatabilirsiniz.");
            }
            else
            {
                BannerOvercharge.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void OnOverchargeAlert(int percent)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            SystemSounds.Exclamation.Play();
            _notifyIcon?.ShowBalloonTip(3000, Loc.T("NitroLite"), Loc.T($"Pil %{percent} seviyesine ulaştı (%80 eşiği aşıldı)."), System.Windows.Forms.ToolTipIcon.Warning);
        });
    }

    private void OnDischargeTargetReached()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            BtnToggleDischarge.Content = Loc.T("Deşarj İşlemini Başlat");
            BtnToggleDischarge.Background = (SolidColorBrush)FindResource("AccentCyan");
            TxtDischargeStatus.Text = Loc.T("Hedef seviyeye (%80) ulaşıldı. Şarj adaptörünü bağlayabilirsiniz.");
            TxtDischargeStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");

            _notifyIcon?.ShowBalloonTip(4000, Loc.T("NitroLite"), Loc.T("Pil seviyesi %80 sınırına ulaştı. Adaptörü bağlayabilirsiniz."), System.Windows.Forms.ToolTipIcon.Info);
            ShowToast(Loc.T("Pil seviyesi %80 hedef sınırına indirildi."), trayBalloon: false);
        });
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

    private bool? _lastHighPowerAllowed;

    /// <summary>
    /// Locks the Turbo/Performance cards on battery or below 40 % (NitroSense rule) and says why.
    /// Called on mode changes, battery status updates and the periodic state poll.
    /// </summary>
    private void UpdateHighPowerAvailabilityUi()
    {
        var (onAc, percent) = PowerStateProvider();
        bool allowed = ModePolicy.IsHighPowerAllowed(onAc, percent);
        if (_lastHighPowerAllowed == allowed) return;
        _lastHighPowerAllowed = allowed;

        foreach (var card in new[] { BtnModeExtreme, BtnModeTurbo })
        {
            if (card == null) continue;
            card.IsEnabled = allowed;
            card.Opacity = allowed ? 1.0 : 0.45;
            card.ToolTip = allowed ? null : ModePolicy.HighPowerBlockedMessage;
        }

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

    private void SetLightingSlidersSilently(Action update)
    {
        bool previous = _isSyncingLightingUi;
        _isSyncingLightingUi = true;
        try { update(); }
        finally { _isSyncingLightingUi = previous; }
    }

    private static bool IsLightingOff(LightingConfig cfg) =>
        cfg.Brightness == 0 || cfg.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends the current <see cref="_lighting"/> to AASSvc and reports a failure to the user.
    /// Cancels any pending debounced slider apply, since this call already carries the latest state.
    /// </summary>
    private async Task<bool> ApplyLightingToDeviceAsync(string failurePrefix)
    {
        _lightingApplyTimer?.Stop();
        var cfg = _lighting;
        bool ok = IsLightingOff(cfg)
            ? await _agentClient.TurnOffLightingAsync()
            : await _agentClient.SetLightingAsync(cfg.Effect, cfg.Brightness, cfg.Speed, cfg.Direction, cfg.Color, cfg.LEDs.ToArray());
        if (!ok && !_isExiting)
        {
            ShowToast(Loc.T($"{failurePrefix}: klavye komutu reddedildi veya Acer servisine ulaşılamadı. Ayar kaydedilmedi."), false);
        }
        else if (ok && _agentClient.LastLightingPartialFailure && !_isExiting)
        {
            ShowToast(Loc.T("Işık komutunu iki hedeften (genel / klavye) yalnızca biri kabul etti; ayar kısmen uygulanmış olabilir."), false);
        }

        // Remember the last lit configuration so brightness/effect changes after "off" can bring it back.
        if (ok && !IsLightingOff(cfg))
        {
            _appSettings.LastActiveLighting = LightingPolicy.Clone(cfg);
        }
        return ok;
    }

    /// <summary>Any manual change means the lighting no longer matches the selected preset: clear the selection
    /// (so re-selecting the same preset applies it again) and stop saving its name.</summary>
    private void MarkLightingCustomized()
    {
        _appSettings.SelectedRgbPreset = null;
        if (CmbRgbPresets?.SelectedItem == null) return;
        bool wasApplying = _isApplyingPreset;
        _isApplyingPreset = true;
        try { CmbRgbPresets.SelectedItem = null; }
        finally { _isApplyingPreset = wasApplying; }
    }

    private static WpfBrush SolidOrFallback(string? hex)
    {
        try { return (new BrushConverter().ConvertFromString(hex ?? "#000000") as WpfBrush) ?? System.Windows.Media.Brushes.Black; }
        catch { return System.Windows.Media.Brushes.Black; }
    }

    /// <summary>Rainbow sample for effects that use the keyboard's own colours (each zone shifted).</summary>
    private static WpfBrush RainbowBrush(int zone)
    {
        var hues = new[] { "#FF2A55", "#FFAA00", "#00FFA3", "#00E5FF", "#BD00FF", "#FF2A55" };
        var brush = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0.5), EndPoint = new WpfPoint(1, 0.5) };
        for (int i = 0; i < 3; i++)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hues[(zone + i) % hues.Length]);
            brush.GradientStops.Add(new GradientStop(c, i / 2.0));
        }
        return brush;
    }

    /// <summary>
    /// Shows what the keyboard will actually do: zone colours only for STATIC, one colour for the other
    /// colour effects, a rainbow sample for WAVE/NEON (which ignore the chosen colour), black when off.
    /// </summary>
    private void UpdatePreviewZones(LightingConfig cfg)
    {
        var zones = new[] { PreviewZone1, PreviewZone2, PreviewZone3, PreviewZone4 };
        string note;
        for (int i = 0; i < zones.Length; i++)
        {
            if (zones[i] == null) continue;
            if (IsLightingOff(cfg)) zones[i].Background = TryFindResource("Ink") as SolidColorBrush ?? System.Windows.Media.Brushes.Black;
            else if (!LightingPolicy.EffectUsesColor(cfg.Effect)) zones[i].Background = RainbowBrush(i);
            else if (LightingPolicy.EffectUsesZoneColors(cfg.Effect)) zones[i].Background = SolidOrFallback(i < cfg.LEDs.Count ? cfg.LEDs[i] : cfg.Color);
            else zones[i].Background = SolidOrFallback(cfg.Color);

        }

        if (IsLightingOff(cfg))
            note = Loc.T("Aydınlatma kapalı. Parlaklığı artırmak, bir efekt ya da renk seçmek son ayarla geri açar.");
        else if (!LightingPolicy.EffectUsesColor(cfg.Effect))
            note = Loc.T("Bu efekt klavyenin kendi renk döngüsünü kullanır; seçilen renk uygulanmaz.");
        else if (!LightingPolicy.EffectUsesZoneColors(cfg.Effect))
            note = Loc.T("Bu efekt tek renk kullanır; bölgelere ayrı renk yalnızca Sabit Renk (STATIC) efektinde verilebilir.");
        else
            note = "";

        if (TxtZonePreviewNote != null)
        {
            TxtZonePreviewNote.Text = note;
            TxtZonePreviewNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool _isSyncingDirectionUi;

    /// <summary>Shows the direction picker only for effects that support it, with only the allowed directions.</summary>
    private void UpdateLightingControlsForEffect(LightingConfig cfg)
    {
        var allowed = IsLightingOff(cfg) ? Array.Empty<int>() : LightingPolicy.AllowedDirections(cfg.Effect);
        if (PnlRgbDirection != null) PnlRgbDirection.Visibility = allowed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (CmbRgbDirection != null)
        {
            _isSyncingDirectionUi = true;
            try
            {
                CmbRgbDirection.Items.Clear();
                foreach (var d in allowed)
                {
                    CmbRgbDirection.Items.Add(new ComboBoxItem { Content = LightingPolicy.DirectionName(d), Tag = d });
                }
                int idx = allowed.ToList().IndexOf(cfg.Direction);
                CmbRgbDirection.SelectedIndex = allowed.Count > 0 ? Math.Max(0, idx) : -1;
            }
            finally
            {
                _isSyncingDirectionUi = false;
            }
        }

        if (TxtRgbEffectNote != null)
        {
            string note = IsLightingOff(cfg) || LightingPolicy.EffectUsesColor(cfg.Effect)
                ? ""
                : Loc.T("Renk butonları bu efektte kullanılmaz; bir renk seçerseniz Sabit Renk'e geçilir.");
            TxtRgbEffectNote.Text = note;
            TxtRgbEffectNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PersistLightingState()
    {
        SaveSettings();
        SaveActiveRgbState();
    }

    /// <summary>Coalesces rapid slider changes into one lighting command + one settings write.</summary>
    private void ScheduleLightingApply()
    {
        if (_lightingApplyTimer == null)
        {
            _lightingApplyTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = LightingApplyDebounce };
            _lightingApplyTimer.Tick += OnLightingApplyTimerTick;
        }
        _lightingApplyTimer.Stop();
        _lightingApplyTimer.Start();
    }

    private async void OnLightingApplyTimerTick(object? sender, EventArgs e)
    {
        _lightingApplyTimer?.Stop();
        if (_isExiting || Dispatcher.HasShutdownStarted) return;
        try
        {
            if (await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma ayarı uygulanamadı")))
            {
                PersistLightingState();
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma ayarı uygulanamadı: {ex.Message}"), false);
        }
    }

    private void UpdateLightingUi(LightingConfig cfg)
    {
        SetLightingSlidersSilently(() =>
        {
            SliderBrightness.Value = cfg.Brightness;
            SliderSpeed.Value = cfg.Speed;
        });
        TxtBrightnessVal.Text = IsLightingOff(cfg) ? Loc.T("0 / 9 (Kapalı)") : $"{cfg.Brightness} / 9";
        TxtSpeedVal.Text = $"{cfg.Speed} / 9";

        UpdatePreviewZones(cfg);
        UpdateLightingControlsForEffect(cfg);
    }

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
                System.Diagnostics.Trace.WriteLine("Eco revert on exit incomplete; persisted state will be restored next start.");
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

    internal async Task<bool> SetManualFanModeAsync(FanMode mode)
    {
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            bool ok = await _agentClient.SetFanModeAsync(mode);
            if (ok)
            {
                _currentFanMode = mode;
                UpdateDesktopBaselineForManualChange(fan: mode);
                ShowToast(mode switch
                {
                    FanMode.Auto => Loc.T("Otomatik fan eğrisi etkinleştirildi."),
                    FanMode.Max => Loc.T("Maksimum fan devri etkinleştirildi."),
                    _ => Loc.T($"Özel fan hızı: CPU %{_agentClient.CustomFanCpuPercent}, GPU %{_agentClient.CustomFanGpuPercent}.")
                });
            }
            else
            {
                ShowToast(Loc.T("Fan modu uygulanamadı."), false);
            }
            return ok;
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Fan modu uygulanamadı: {ex.Message}"), false);
            return false;
        }
        finally
        {
            _gameProfileTransitionLock.Release();
        }
    }

    private async void BtnFanAuto_Click(object sender, RoutedEventArgs e)
    {
        await SetManualFanModeAsync(FanMode.Auto);
    }

    private async void BtnFanMax_Click(object sender, RoutedEventArgs e)
    {
        await SetManualFanModeAsync(FanMode.Max);
    }

    private async void BtnPresetColor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is WpfButton btn && btn.Tag is string hex)
            {
                if (hex == "#000000")
                {
                    CancelActiveLightingReassertion();
                    await TurnOffLightsAsync();
                    return;
                }
                await ApplyColorToAllZonesAsync(hex);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma rengi ayarlanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// One colour for the whole keyboard. Effects that take a colour (STATIC, BREATHING, ZOOM…) keep running with
    /// the new colour; WAVE/NEON ignore colours, so the keyboard switches to STATIC. When the lights are off, the
    /// last lit settings come back with this colour.
    /// </summary>
    private async Task ApplyColorToAllZonesAsync(string hex)
    {
        CancelActiveLightingReassertion();
        if (IsLightingOff(_lighting))
        {
            _lighting = LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting);
        }
        if (!LightingPolicy.EffectUsesColor(_lighting.Effect))
        {
            _lighting.Effect = "STATIC";
        }
        _lighting.Color = hex;
        _lighting.LEDs = new List<string> { hex, hex, hex, hex };

        MarkLightingCustomized();
        UpdateLightingUi(_lighting);
        SuppressEffectComboEvents(() => UpdateEffectComboBox(_lighting.Effect));
        if (!await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma rengi ayarlanamadı"))) return;
        _appSettings.IsBacklightOff = false;
        PersistLightingState();
    }

    /// <summary>Opens the Windows colour picker; returns "#RRGGBB" or null when cancelled.</summary>
    private static string? PickColor(string? initialHex)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(initialHex ?? LightingPolicy.DefaultColor);
            dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }
        catch { }
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}"
            : null;
    }

    private async void BtnCustomColor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var hex = PickColor(IsLightingOff(_lighting) ? null : _lighting.Color);
            if (hex == null) return;
            if (hex == "#000000")
            {
                await TurnOffLightsAsync();
                return;
            }
            await ApplyColorToAllZonesAsync(hex);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma rengi ayarlanamadı: {ex.Message}"), false);
        }
    }

    /// <summary>Click a zone to give it its own colour. Zone colours only exist in STATIC, so it switches to STATIC.</summary>
    private async void PreviewZone_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is not Border zone || !int.TryParse(zone.Tag?.ToString(), out int index) || index < 0 || index > 3) return;
            CancelActiveLightingReassertion();

            var baseCfg = IsLightingOff(_lighting) ? LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting) : LightingPolicy.Clone(_lighting);
            var leds = baseCfg.LEDs.Count >= 4 ? baseCfg.LEDs.Take(4).ToList() : Enumerable.Repeat(baseCfg.Color, 4).ToList();
            // Coming from a single-colour or rainbow effect, every zone starts from the visible colour.
            if (!LightingPolicy.EffectUsesZoneColors(baseCfg.Effect))
            {
                var start = LightingPolicy.EffectUsesColor(baseCfg.Effect) ? baseCfg.Color : LightingPolicy.DefaultColor;
                leds = Enumerable.Repeat(start, 4).ToList();
            }

            var hex = PickColor(leds[index]);
            if (hex == null) return;

            leds[index] = hex;
            baseCfg.Effect = "STATIC";
            baseCfg.LEDs = leds;
            baseCfg.Color = leds[0];
            _lighting = baseCfg;

            MarkLightingCustomized();
            UpdateLightingUi(_lighting);
            SuppressEffectComboEvents(() => UpdateEffectComboBox("STATIC"));
            if (!await ApplyLightingToDeviceAsync(Loc.T("Bölge rengi ayarlanamadı"))) return;
            _appSettings.IsBacklightOff = false;
            PersistLightingState();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bölge rengi ayarlanamadı: {ex.Message}"), false);
        }
    }

    private async void CmbRgbDirection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingDirectionUi) return;
            if (CmbRgbDirection.SelectedItem is not ComboBoxItem item || item.Tag is not int direction) return;
            if (_lighting.Direction == direction) return;
            CancelActiveLightingReassertion();
            _lighting.Direction = direction;
            MarkLightingCustomized();
            if (!await ApplyLightingToDeviceAsync(Loc.T("Efekt yönü ayarlanamadı"))) return;
            PersistLightingState();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Efekt yönü ayarlanamadı: {ex.Message}"), false);
        }
    }

    private async void BtnTurnOffLights_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await TurnOffLightsAsync();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma kapatılamadı: {ex.Message}"), false);
        }
    }

    private async Task TurnOffLightsAsync()
    {
        CancelActiveLightingReassertion();
        // Keep what was lit so brightness/effect/colour changes can bring it back later.
        if (!IsLightingOff(_lighting))
        {
            _appSettings.LastActiveLighting = LightingPolicy.Clone(_lighting);
        }
        _lighting.Brightness = 0;
        _lighting.Effect = "OFF";
        _lighting.Color = "#000000";
        _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };

        UpdateLightingUi(_lighting);
        SuppressEffectComboEvents(() => UpdateEffectComboBox("OFF"));

        if (!await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma kapatılamadı"))) return;

        _appSettings.IsBacklightOff = true;
        _appSettings.KeyboardLighting = _lighting;

        var presets = _rgbProfileService.GetPresets();
        var offPreset = presets.FirstOrDefault(p => p.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase) || p.Brightness == 0);
        if (offPreset != null)
        {
            _appSettings.SelectedRgbPreset = offPreset.Name;
            if (CmbRgbPresets != null)
            {
                // Already applied above; do not let the preset combo send the same command again.
                bool wasApplying = _isApplyingPreset;
                _isApplyingPreset = true;
                try { CmbRgbPresets.SelectedItem = offPreset.Name; }
                finally { _isApplyingPreset = wasApplying; }
            }
        }

        PersistLightingState();
    }

    private void SuppressEffectComboEvents(Action update)
    {
        bool wasApplying = _isApplyingPreset;
        _isApplyingPreset = true;
        try { update(); }
        finally { _isApplyingPreset = wasApplying; }
    }

    private async void CmbRgbEffect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (!IsLoaded || _isInitializingUi || _isApplyingPreset) return;
            CancelActiveLightingReassertion();
            if (CmbRgbEffect.SelectedItem is ComboBoxItem item && item.Content is string text)
            {
                var effectName = text.Split(' ')[0];
                if (effectName.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                {
                    await TurnOffLightsAsync();
                    return;
                }

                // From "off" (black colours), bring the last lit settings back with the chosen effect;
                // otherwise just change the effect.
                if (IsLightingOff(_lighting))
                {
                    _lighting = LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting, effect: effectName);
                }
                else
                {
                    _lighting.Effect = effectName;
                    if (!LightingPolicy.AllowedDirections(effectName).Contains(_lighting.Direction)) _lighting.Direction = 1;
                }

                MarkLightingCustomized();
                UpdateLightingUi(_lighting);

                if (!await ApplyLightingToDeviceAsync(Loc.T("Aydınlatma efekti ayarlanamadı"))) return;
                _appSettings.IsBacklightOff = false;
                PersistLightingState();
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Aydınlatma efekti ayarlanamadı: {ex.Message}"), false);
        }
    }

    // Sliders only update the preview and in-memory state immediately; the device command and the
    // settings write are debounced (ScheduleLightingApply) so a drag sends one command, not one per tick.
    private void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingLightingUi || TxtBrightnessVal == null) return;
        CancelActiveLightingReassertion();
        var val = (int)e.NewValue;

        if (val > 0 && IsLightingOff(_lighting))
        {
            // Raising brightness while off: bring back the last lit settings at this brightness
            // (before, the command stayed "OFF" and the keyboard never lit up).
            _lighting = LightingPolicy.ResumeFromOff(_appSettings.LastActiveLighting, brightness: val);
            SuppressEffectComboEvents(() => UpdateEffectComboBox(_lighting.Effect));
        }
        else
        {
            if (val == 0 && !IsLightingOff(_lighting))
            {
                _appSettings.LastActiveLighting = LightingPolicy.Clone(_lighting);
            }
            _lighting.Brightness = val;
        }

        _appSettings.IsBacklightOff = IsLightingOff(_lighting);
        _appSettings.KeyboardLighting = _lighting;
        MarkLightingCustomized();
        UpdateLightingUi(_lighting);
        ScheduleLightingApply();
    }

    private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingLightingUi || TxtSpeedVal == null) return;
        CancelActiveLightingReassertion();
        var val = (int)e.NewValue;
        TxtSpeedVal.Text = $"{val} / 9";
        _lighting.Speed = val;
        MarkLightingCustomized();
        ScheduleLightingApply();
    }

    private bool _isSyncingBacklightTimeoutUi;

    private void SetBacklightTimeoutCheckboxSilently(bool isChecked)
    {
        if (ChkBacklightTimeout == null) return;
        _isSyncingBacklightTimeoutUi = true;
        try { ChkBacklightTimeout.IsChecked = isChecked; }
        finally { _isSyncingBacklightTimeoutUi = false; }
    }

    /// <summary>The 30 s idle-off setting lives on the device (EC); mirror it instead of assuming "off".</summary>
    private async Task SyncBacklightTimeoutFromDeviceAsync()
    {
        try
        {
            var state = await _agentClient.GetBacklightTimeoutAsync();
            if (state.HasValue && !Dispatcher.HasShutdownStarted)
            {
                SetBacklightTimeoutCheckboxSilently(state.Value);
            }
        }
        catch { }
    }

    private async void ChkBacklightTimeout_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || _isSyncingBacklightTimeoutUi) return;
        var enable = ChkBacklightTimeout.IsChecked == true;
        bool ok;
        try
        {
            ok = await _agentClient.SetBacklightTimeoutAsync(enable);
        }
        catch
        {
            ok = false;
        }

        if (!ok)
        {
            SetBacklightTimeoutCheckboxSilently(!enable);
            ShowToast(Loc.T("30 sn aydınlatma zaman aşımı değiştirilemedi: klavye komutu reddedildi veya Acer servisine ulaşılamadı."), false);
        }
    }

    private readonly CareCenterClient _careCenterClient = new();
    private bool _isSyncingWatchdogUi;

    private void SetWatchdogCheckboxSilently(bool isChecked)
    {
        if (ChkWatchdog == null) return;
        _isSyncingWatchdogUi = true;
        try { ChkWatchdog.IsChecked = isChecked; }
        finally { _isSyncingWatchdogUi = false; }
        _batteryService.WatchdogEnabled = isChecked;
    }

    /// <summary>
    /// The 80 % limit is owned by Acer Care Center; show what it actually reports instead of assuming.
    /// Runs in the background so a missing AcerCCAgent cannot delay startup.
    /// </summary>
    private async Task SyncChargeLimitFromCareCenterAsync()
    {
        try
        {
            var mode = await _careCenterClient.GetBatteryChargeModeAsync();
            if (!mode.HasValue || Dispatcher.HasShutdownStarted) return;
            bool limited = mode.Value == BatteryChargeMode.Optimized80;
            await Dispatcher.InvokeAsync(() =>
            {
                SetWatchdogCheckboxSilently(limited);
                if (_appSettings.BatteryWatchdogEnabled != limited)
                {
                    _appSettings.BatteryWatchdogEnabled = limited;
                    SaveSettings();
                }
            });
        }
        catch { }
    }

    private async void ChkWatchdog_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ChkWatchdog == null) return;
        bool enable = ChkWatchdog.IsChecked == true;
        _batteryService.WatchdogEnabled = enable;
        if (_isInitializingUi || _isSyncingWatchdogUi) return;

        ChkWatchdog.IsEnabled = false;
        try
        {
            bool ok = await _careCenterClient.SetBatteryChargeModeAsync(enable ? BatteryChargeMode.Optimized80 : BatteryChargeMode.Full);
            if (ok)
            {
                _appSettings.BatteryWatchdogEnabled = enable;
                SaveSettings();
                ShowToast(enable ? Loc.T("%80 şarj sınırı etkinleştirildi (Acer Care Center).") : Loc.T("Şarj sınırı kapatıldı; pil %100'e kadar şarj olacak."));
            }
            else
            {
                SetWatchdogCheckboxSilently(!enable);
                ShowToast(Loc.T($"Şarj sınırı değiştirilemedi: {_careCenterClient.LastError ?? "bilinmeyen hata"}"), false);
            }
        }
        catch (Exception ex)
        {
            SetWatchdogCheckboxSilently(!enable);
            ShowToast(Loc.T($"Şarj sınırı değiştirilemedi: {ex.Message}"), false);
        }
        finally
        {
            ChkWatchdog.IsEnabled = true;
        }
    }

    private void BtnToggleDischarge_Click(object sender, RoutedEventArgs e)
    {
        if (_batteryService.IsDischarging)
        {
            _batteryService.StopSafeDischarge(); // UI follows via DischargeStateChanged
            return;
        }

        if (_batteryService.CurrentStatus.IsPluggedIn)
        {
            var res = System.Windows.MessageBox.Show(this,
                Loc.T("Adaptör bağlıyken batarya deşarj edilemez.\n\nAdaptörü çıkardıktan sonra 'Evet'e tıklayın."),
                Loc.T("Adaptör Bağlantısı"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }

        // The service re-reads the battery; it refuses on AC or at <= 80 %, and the UI only shows "active" if it started.
        if (!_batteryService.StartSafeDischarge())
        {
            var st = _batteryService.CurrentStatus;
            ShowToast(st.IsPluggedIn
                ? Loc.T("Adaptör hâlâ bağlı; deşarj başlatılmadı.")
                : Loc.T($"Pil seviyesi (%{st.BatteryPercent}) zaten %80 veya altında; deşarja gerek yok."), isSuccess: false);
        }
    }

    /// <summary>Keeps the discharge card in sync with the service, including stops caused by reconnecting the adapter.</summary>
    private void OnDischargeStateChanged(bool active)
    {
        if (Dispatcher.HasShutdownStarted) return;
        // Invoke (not BeginInvoke): keeps the order with DischargeTargetReached, whose message must win.
        Dispatcher.Invoke(new Action(() =>
        {
            if (BtnToggleDischarge == null || TxtDischargeStatus == null) return;
            if (active)
            {
                BtnToggleDischarge.Content = Loc.T("Deşarjı Durdur");
                BtnToggleDischarge.Background = (SolidColorBrush)FindResource("AccentRed");
                TxtDischargeStatus.Text = Loc.T("Deşarj işlemi aktif: Pil seviyesi %80 sınırına indiriliyor...");
                TxtDischargeStatus.Foreground = (SolidColorBrush)FindResource("AccentCyan");
            }
            else
            {
                BtnToggleDischarge.Content = Loc.T("Deşarj İşlemini Başlat");
                BtnToggleDischarge.Background = (SolidColorBrush)FindResource("AccentCyan");
                bool plugged = _batteryService.CurrentStatus.IsPluggedIn;
                TxtDischargeStatus.Text = plugged ? Loc.T("Durum: Durdu (adaptör bağlandı).") : Loc.T("Durum: Durduruldu.");
                TxtDischargeStatus.Foreground = (SolidColorBrush)FindResource("TextSecondary");
            }
        }));
    }

    private bool _serviceOperationBusy;

    // Service registry reads, the SCM queries and the schtasks call run off the UI thread.
    private async Task RefreshServiceListAsync()
    {
        var (statuses, nsRunning, launcherEnabled) = await Task.Run(() =>
            (_debloatService.GetServiceStatuses(), _debloatService.IsNitroSenseRunning(), _debloatService.IsNitroSenseLauncherEnabled()));

        if (ListServiceStatuses != null) ListServiceStatuses.ItemsSource = statuses;

        if (TxtNitroSenseRunningBadge != null)
        {
            TxtNitroSenseRunningBadge.Text = nsRunning ? Loc.T("Açık") : Loc.T("Kapalı");
            TxtNitroSenseRunningBadge.Foreground = nsRunning
                ? (SolidColorBrush)FindResource("AccentRed")
                : (SolidColorBrush)FindResource("TextMuted");
        }
        if (TxtNitroSenseLauncherState != null)
        {
            TxtNitroSenseLauncherState.Text = launcherEnabled switch
            {
                true => Loc.T("Windows açılışında NitroSense'i başlatan görev (NitroSenseLauncher): açık"),
                false => Loc.T("Windows açılışında NitroSense'i başlatan görev (NitroSenseLauncher): kapalı"),
                null => Loc.T("NitroSense'i açılışta başlatan görev bulunamadı")
            };
        }
        // Dimmed rather than disabled: the button template renders a disabled button as a blank light box.
        if (BtnKillNitroSense != null) BtnKillNitroSense.Opacity = nsRunning ? 1.0 : 0.45;
        UpdateServiceButtons();
    }

    private void UpdateServiceButtons()
    {
        // Dimmed, not disabled (see above); the click handlers ignore clicks while an operation runs.
        foreach (var b in new[] { BtnApplyDebloat, BtnRestoreServices })
        {
            if (b != null) b.Opacity = _serviceOperationBusy ? 0.45 : 1.0;
        }
    }

    private async void BtnKillNitroSense_Click(object sender, RoutedEventArgs e)
    {
        if (!_debloatService.IsNitroSenseRunning())
        {
            await RefreshServiceListAsync();
            ShowToast(Loc.T("NitroSense zaten kapalı."));
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("NitroSense ve yardımcı işlemleri kapatılacak. Kaydedilmemiş bir NitroSense ayarı varsa kaybolur.\n\nDevam edilsin mi?"),
            Loc.T("NitroSense'i Kapat"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        bool closed = await Task.Run(() => _debloatService.KillNitroSenseProcesses());
        await RefreshServiceListAsync();
        ShowToast(closed ? Loc.T("NitroSense kapatıldı.") : Loc.T("NitroSense kapatılamadı (yönetici olarak çalışıyor olabilir)."), closed);
    }

    private async void BtnApplyDebloat_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        try
        {
            if (await Task.Run(() => _debloatService.AreAllInstalledServicesDisabled()) && !_debloatService.HasRestorableBackup())
            {
                ShowToast(DebloatService.AlreadyDisabledMessage);
                return;
            }

            var confirm = System.Windows.MessageBox.Show(this,
                Loc.T("Listelenen Acer arka plan servisleri durdurulup devre dışı bırakılacak ve NitroSense'in açılışta başlatılması kapatılacak. " +
                "Mevcut ayarlar önce yedeklenir; \"Servisleri Geri Yükle\" ile geri dönebilirsiniz.\n\n" +
                "NitroLite'ın kullandığı Acer Agent, Care Center ve Lighting servislerine dokunulmaz. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
                Loc.T("Acer Servisleri"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _serviceOperationBusy = true;
            UpdateServiceButtons();
            var ok = await _debloatService.ApplyTier1DebloatAsync();
            _serviceOperationBusy = false;
            await RefreshServiceListAsync();
            if (ok)
            {
                ShowToast(Loc.T("Acer arka plan servisleri devre dışı bırakıldı."));
            }
            else
            {
                var msg = !string.IsNullOrEmpty(_debloatService.LastError)
                    ? Loc.T($"Servisler devre dışı bırakılamadı: {_debloatService.LastError}")
                    : Loc.T("Servisler devre dışı bırakılamadı (yönetici izni verilmemiş olabilir).");
                ShowToast(msg, false);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Servisler yapılandırılamadı: {ex.Message}"), false);
        }
        finally
        {
            _serviceOperationBusy = false;
            UpdateServiceButtons();
        }
    }

    private async void BtnRestoreServices_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        try
        {
            bool hasBackup = _debloatService.HasRestorableBackup();
            var question = hasBackup
                ? Loc.T("Servisler, devre dışı bırakılmadan önce kaydedilen haline döndürülecek. Yönetici izni istenecek.\n\nDevam edilsin mi?")
                : Loc.T("Servislerin önceki hali için kayıtlı bir yedek yok.\n\nServisler Acer'in fabrika ayarına (Otomatik başlat) döndürülüp başlatılacak. " +
                  "NitroSense'in açılışta başlatma ayarına dokunulmaz. Yönetici izni istenecek.\n\nDevam edilsin mi?");
            var confirm = System.Windows.MessageBox.Show(this, question, Loc.T("Servisleri Geri Yükle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _serviceOperationBusy = true;
            UpdateServiceButtons();
            var ok = hasBackup
                ? await _debloatService.RestoreTier1ServicesAsync()
                : await _debloatService.RestoreFactoryDefaultsAsync();
            _serviceOperationBusy = false;
            await RefreshServiceListAsync();
            if (ok)
            {
                ShowToast(hasBackup ? Loc.T("Önceki servis yapılandırması geri yüklendi.") : Loc.T("Servisler Acer fabrika ayarına döndürüldü."));
            }
            else
            {
                var msg = !string.IsNullOrEmpty(_debloatService.LastError)
                    ? Loc.T($"Servisler geri yüklenemedi: {_debloatService.LastError}")
                    : Loc.T("Servisler geri yüklenemedi (yönetici izni verilmemiş olabilir).");
                ShowToast(msg, false);
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Servisler geri yüklenemedi: {ex.Message}"), false);
        }
        finally
        {
            _serviceOperationBusy = false;
            UpdateServiceButtons();
        }
    }

    private async void BtnFixLighting_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("AcerLightingService kayıt defteri yolu ve başlangıç türü sürücü deposuyla eşitlenecek, ardından klavye servisi yeniden başlatılacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Klavye RGB Servisini Onar"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Klavye aydınlatma servisi onarılıyor..."));
        try
        {
            var res = await SystemToolsService.FixKeyboardLightingAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
            await RefreshServiceListAsync();
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Onarım hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnFixAcerServices_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("AcerCentralService ve AcerLightingService en güncel sürücü paketine bağlanacak, çakışan eski servis süreçleri kapatılıp servisler yeniden başlatılacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Acer & NitroSense Servislerini Onar"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Acer arka plan servisleri güncelleniyor ve onarılıyor..."));
        try
        {
            var res = await SystemToolsService.FixAcerServicesAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
            await RefreshServiceListAsync();
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Onarım hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnRestoreMux_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Kayıt defterinde discrete_gpu_support (1) değeri doğrulanacak, eski geçici logon görevleri temizlenecek ve Quick Access servisi başlatılacak. NitroSense açıksa kapatılacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("MUX Switch Kaydını Onar"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("MUX switch kaydı ve servisleri düzenleniyor..."));
        try
        {
            var res = await SystemToolsService.RestoreMuxSwitchAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"MUX onarım hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnOptimizePc_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Gereksiz müşteri deneyimi telemetri görevleri kapatılacak, HAGS (GPU donanım hızlandırmalı zamanlama) etkinleştirilecek, NVMe TRIM çalıştırılacak ve boşta kalan arka plan servisleri manuel moda alınacak. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Windows & Sistem Optimizasyonu"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Sistem ve arka plan optimizasyonları uygulanıyor..."));
        try
        {
            var res = await SystemToolsService.OptimizePcAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Optimizasyon hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private async void BtnRestoreOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceOperationBusy) return;
        var confirm = System.Windows.MessageBox.Show(this,
            Loc.T("Optimizasyonun değiştirdiği servis başlangıç türleri, HAGS ayarı ve telemetri görevleri yedekten geri yüklenecek. Yönetici izni istenecek.\n\nDevam edilsin mi?"),
            Loc.T("Optimizasyonu Geri Al"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetRepairToolBusy(true, Loc.T("Optimizasyon öncesi ayarlar geri yükleniyor..."));
        try
        {
            var res = await SystemToolsService.RestoreOptimizePcAsync();
            SetRepairToolStatus(res.Success, res.Message);
            ShowToast(res.Message, res.Success);
        }
        catch (Exception ex)
        {
            SetRepairToolStatus(false, Loc.T($"Hata: {ex.Message}"));
            ShowToast(Loc.T($"Geri alma hatası: {ex.Message}"), false);
        }
        finally
        {
            SetRepairToolBusy(false);
        }
    }

    private void SetRepairToolBusy(bool busy, string? status = null)
    {
        _serviceOperationBusy = busy;
        UpdateServiceButtons();
        if (BtnFixLighting != null) BtnFixLighting.IsEnabled = !busy;
        if (BtnFixAcerServices != null) BtnFixAcerServices.IsEnabled = !busy;
        if (BtnRestoreMux != null) BtnRestoreMux.IsEnabled = !busy;
        if (BtnOptimizePc != null) BtnOptimizePc.IsEnabled = !busy;
        if (BtnRestoreOptimize != null) BtnRestoreOptimize.IsEnabled = !busy;

        if (TxtRepairToolsStatus != null)
        {
            if (!string.IsNullOrWhiteSpace(status))
            {
                TxtRepairToolsStatus.Text = status;
                TxtRepairToolsStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentCyan");
                TxtRepairToolsStatus.Visibility = Visibility.Visible;
            }
        }
    }

    private void SetRepairToolStatus(bool success, string message)
    {
        if (TxtRepairToolsStatus != null)
        {
            TxtRepairToolsStatus.Text = (success ? "● " : "▲ ") + message;
            TxtRepairToolsStatus.Foreground = (System.Windows.Media.Brush)FindResource(success ? "AccentGreen" : "AccentRed");
            TxtRepairToolsStatus.Visibility = Visibility.Visible;
        }
    }

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

    private async void BtnCleanRamCache_Click(object sender, RoutedEventArgs e)
    {
        if (_ramCleanBusy) return;
        _ramCleanBusy = true;
        if (BtnCleanRamCache != null) BtnCleanRamCache.Opacity = 0.45;
        try
        {
            _ramCleanResultShown = false;
            SetRamCleanStatus(Loc.T(" (Boşaltılıyor…)"), "TextSecondary");
            var res = await _memoryService.CleanMemoryAsync();
            ShowMemoryCleanResult(res);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bekleme belleği boşaltılamadı: {ex.Message}"), false);
        }
        finally
        {
            _ramCleanBusy = false;
            await RefreshRamCleanStateAsync(keepResultText: true);
        }
    }

    /// <summary>Opens the official WinMemoryCleaner release page; only shown when the bundled copy is missing.</summary>
    private void BtnGetMemoryCleaner_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(MemoryCleanerService.ToolReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bağlantı açılamadı: {ex.Message}"), false);
        }
    }

    private void ShowMemoryCleanResult(MemoryCleanerResult res)
    {
        if (res.ToolMissing)
        {
            ShowToast(Loc.T("WinMemoryCleaner kurulu değil. Buton, resmi indirme sayfasını açar."), false);
            SetRamCleanStatus(Loc.T(" (Araç kurulu değil)"), "TextSecondary");
            return;
        }
        if (res.ElevationRefused)
        {
            ShowToast(Loc.T("Yönetici izni verilmedi; bekleme belleği boşaltılmadı."), false);
            SetRamCleanStatus(Loc.T(" (İzin verilmedi)"), "TextSecondary");
            return;
        }
        if (!res.Cleaned)
        {
            ShowToast(Loc.T($"Bekleme belleği boşaltılamadı: {res.Error}"), false);
            SetRamCleanStatus(Loc.T(" (Başarısız)"), "AccentRed");
            return;
        }

        _ramCleanResultShown = true;
        string freedStr = res.FreedMegabytes >= 1024
            ? FormattableString.Invariant($"{res.FreedMegabytes / 1024.0:F1} GB")
            : FormattableString.Invariant($"{res.FreedMegabytes:F0} MB");
        if (res.FreedMegabytes <= 0)
        {
            ShowToast(Loc.T("Bekleme belleği zaten temiz (boşaltılacak önbellek yok)."), isSuccess: true, trayBalloon: false);
            SetRamCleanStatus(Loc.T(" (Zaten temiz)"), "AccentGreen");
        }
        else
        {
            ShowToast(Loc.T($"Önbellek boşaltıldı: {freedStr} serbest bırakıldı (kullanılan RAM değişmez)."), isSuccess: true, trayBalloon: false);
            SetRamCleanStatus(Loc.T($" ({freedStr} serbest bırakıldı)"), "AccentGreen");
        }
        UpdateMemoryListText();
    }

    private void SetRamCleanStatus(string text, string brushKey)
    {
        if (TxtRamCleanStatus == null) return;
        TxtRamCleanStatus.Text = text;
        TxtRamCleanStatus.Foreground = (SolidColorBrush)FindResource(brushKey);
    }

    /// <summary>Points the card at whichever state the machine is in: tool present, or a link to get it.</summary>
    private Task RefreshRamCleanStateAsync(bool keepResultText = false)
    {
        bool installed = MemoryCleanerService.FindTool() != null;

        if (BtnCleanRamCache != null) BtnCleanRamCache.Opacity = 1.0;
        if (BtnGetMemoryCleaner != null)
            BtnGetMemoryCleaner.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;

        if (!(keepResultText && _ramCleanResultShown))
        {
            if (installed) SetRamCleanStatus(Loc.T(" (Hazır)"), "AccentGreen");
            else SetRamCleanStatus(Loc.T(" (Araç kurulu değil)"), "TextSecondary");
        }
        UpdateMemoryListText();
        return Task.CompletedTask;
    }

    /// <summary>Live standby/free sizes in the RAM card (readable without administrator rights).</summary>
    private void UpdateMemoryListText()
    {
        if (TxtRamCleanNote == null) return;
        var lists = MemoryCleanerService.ReadMemoryLists();
        string sizes = lists.HasValue
            ? Loc.TInv($"Şu an bekleme listesinde {lists.Value.StandbyGigabytes:F1} GB, boşta {lists.Value.FreeGigabytes:F1} GB var. ")
            : "";
        TxtRamCleanNote.Text = sizes + Loc.T("Windows'un önbellekte tuttuğu (bekleme listesi) belleği boş belleğe çevirir; çalışan programların belleğine dokunmaz, bu yüzden kullanılan RAM çubuğu değişmez. ")
                               + Loc.T("Oyunun kendi bellek sızıntısını geri vermez; boş bellek azaldığında oluşan takılmalara karşı işe yarar.");
    }

    private async void CmbDcPowerMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || CmbDcPowerMode?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (Enum.TryParse<DcPowerProfilePreference>(tag, out var pref))
        {
            _appSettings.DcPowerMode = pref;
            SaveSettings();
            ShowToast(pref == DcPowerProfilePreference.Eco ? Loc.T("Pilde güç tercihi: Eco (sessiz mod, pilde boost kapalı)") : Loc.T("Pilde güç tercihi: Dengeli (mevcut ayarlar korunur)"));
            if (_powerAutomationService != null) await _powerAutomationService.EvaluatePowerStateAsync();
        }
    }

    private async void ChkAutoRefreshRate_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAutoRefreshRate == null) return;
        _appSettings.AutoRefreshRateSwitching = ChkAutoRefreshRate.IsChecked == true;
        if (_powerAutomationService != null) await _powerAutomationService.EvaluatePowerStateAsync();
        SaveSettings();
        ShowToast(_appSettings.AutoRefreshRateSwitching
            ? Loc.T("Otomatik tazeleme etkin: pilde Eco laptop ekranını 60 Hz'e alır, adaptörde önceki hıza döner")
            : Loc.T("Otomatik ekran tazeleme hızı geçişi devre dışı"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Settings card: language, theme, update checks, feedback. The update button in the title bar lives here too.
    // ---------------------------------------------------------------------------------------------------------------

    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _firstUpdateTimer;
    private UpdateInfo? _pendingUpdate;
    private bool _updateBusy;
    private bool _settingsCardReady;

    /// <summary>
    /// Fills the settings card from the loaded settings and starts the update check. Called once the settings are in
    /// memory, so the language and theme lists show the values that are actually in use.
    /// </summary>
    private void SetupSettingsCard()
    {
        if (CmbLanguage == null || CmbTheme == null || ChkAutoUpdateCheck == null) return;

        _isInitializingUi = true;
        try
        {
            CmbLanguage.Items.Clear();
            foreach (var language in Loc.Languages)
                CmbLanguage.Items.Add(new ComboBoxItem { Content = language.NativeName, Tag = language.Code });
            CmbLanguage.SelectedItem = CmbLanguage.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag as string, Loc.Language, StringComparison.OrdinalIgnoreCase))
                ?? CmbLanguage.Items[0];

            CmbTheme.Items.Clear();
            CmbTheme.Items.Add(new ComboBoxItem { Content = Loc.T("Aydınlık"), Tag = AppTheme.Light });
            CmbTheme.Items.Add(new ComboBoxItem { Content = Loc.T("Koyu"), Tag = AppTheme.Dark });
            var theme = ThemeManager.Resolve(_appSettings.Theme);
            CmbTheme.SelectedItem = CmbTheme.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is AppTheme t && t == theme) ?? CmbTheme.Items[0];

            ChkAutoUpdateCheck.IsChecked = _appSettings.AutoUpdateCheck;
            if (TxtSettingsVersion != null) TxtSettingsVersion.Text = UpdateService.CurrentVersionText;
        }
        finally
        {
            _isInitializingUi = false;
        }

        _settingsCardReady = true;
        StartUpdateChecks();
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsCardReady || _isInitializingUi) return;
        if (CmbLanguage?.SelectedItem is not ComboBoxItem item || item.Tag is not string code) return;
        if (string.Equals(code, Loc.Language, StringComparison.OrdinalIgnoreCase)) return;

        _appSettings.Language = code;
        SaveSettings();
        RestartForSettingChange(Loc.T("Dil değişikliği için NitroLite yeniden başlatılıyor…"));
    }

    private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsCardReady || _isInitializingUi) return;
        if (CmbTheme?.SelectedItem is not ComboBoxItem item || item.Tag is not AppTheme theme) return;
        if (theme == ThemeManager.Resolve(_appSettings.Theme)) return;

        _appSettings.Theme = theme;
        SaveSettings();
        RestartForSettingChange(Loc.T("Tema değişikliği için NitroLite yeniden başlatılıyor…"));
    }

    private void ChkAutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsCardReady || _isInitializingUi || ChkAutoUpdateCheck == null) return;
        _appSettings.AutoUpdateCheck = ChkAutoUpdateCheck.IsChecked == true;
        SaveSettings();
        if (_appSettings.AutoUpdateCheck) StartUpdateChecks();
        else StopUpdateChecks();
        ShowToast(_appSettings.AutoUpdateCheck
            ? Loc.T("Güncellemeleri otomatik denetle")
            : Loc.T("Güncelleme denetimi kapalı."));
    }

    /// <summary>
    /// Restarts NitroLite so a setting that is read once at startup takes effect. The single-instance mutex is
    /// released first: otherwise the replacement process would see a running instance and exit immediately.
    /// </summary>
    private void RestartForSettingChange(string message)
    {
        if (_isGameSessionActive)
        {
            // Restarting mid-game would drop the game profile and the fan curve the session is running on.
            ShowToast(Loc.T("Ayar kaydedildi; oyun sırasında yeniden başlatılmaz, bir sonraki açılışta geçerli olur."), false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            ShowToast(Loc.T("NitroLite yeniden başlatılamadı; elle kapatıp açın."), false);
            return;
        }

        ShowToast(message);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            try
            {
                App.ReleaseSingleInstanceMutex();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
            catch (Exception ex)
            {
                ShowToast(Loc.T($"NitroLite yeniden başlatılamadı: {ex.Message}"), false);
                return;
            }
            System.Windows.Application.Current?.Shutdown();
        };
        timer.Start();
    }

    private void StartUpdateChecks()
    {
        if (!_appSettings.AutoUpdateCheck) return;
        if (_firstUpdateTimer != null || _updateTimer != null) return;

        // Thirty seconds after start, then every six hours. A missing network is not something to report.
        _firstUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _firstUpdateTimer.Tick += async (s, e) =>
        {
            _firstUpdateTimer?.Stop();
            _firstUpdateTimer = null;
            await CheckForUpdatesAsync(silent: true);
        };
        _firstUpdateTimer.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (s, e) => await CheckForUpdatesAsync(silent: true);
        _updateTimer.Start();
    }

    private void StopUpdateChecks()
    {
        _firstUpdateTimer?.Stop();
        _firstUpdateTimer = null;
        _updateTimer?.Stop();
        _updateTimer = null;
    }

    /// <summary>
    /// Asks GitHub for a newer release. Silent checks never report a failure; a manual check does.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            if (!UpdateService.IsPackaged)
            {
                // A development build cannot replace itself; the button opens the release page instead.
                if (!silent) OpenUrl(UpdateService.ReleasePageUrl);
                return;
            }

            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                _pendingUpdate = null;
                if (BtnUpdateNow != null) BtnUpdateNow.Visibility = Visibility.Collapsed;
                if (!silent) ShowToast(Loc.T("Güncel sürümü kullanıyorsunuz."));
                return;
            }

            _pendingUpdate = info;
            if (BtnUpdateNow != null && TxtUpdateNow != null)
            {
                TxtUpdateNow.Text = Loc.T($"Güncelle {info.DisplayVersion}");
                BtnUpdateNow.Visibility = Visibility.Visible;
            }
            if (!silent) ShowToast(Loc.T($"Yeni sürüm var: {info.DisplayVersion}"));
        }
        catch (Exception ex)
        {
            if (!silent) ShowToast(Loc.T($"Güncelleme denetlenemedi: {ex.Message}"), false);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = false;
        try
        {
            await CheckForUpdatesAsync(silent: false);
        }
        finally
        {
            if (BtnCheckUpdates != null) BtnCheckUpdates.IsEnabled = true;
        }
    }

    /// <summary>
    /// One click: download, verify against the digest GitHub publishes, swap the executable, restart. Every failure
    /// leaves the current installation untouched.
    /// </summary>
    private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null || _updateBusy) return;
        if (_isGameSessionActive)
        {
            ShowToast(Loc.T("Oyun sırasında güncelleme yapılmaz."), false);
            return;
        }

        var info = _pendingUpdate;
        _updateBusy = true;
        if (BtnUpdateNow != null) BtnUpdateNow.IsEnabled = false;
        string? zip = null;
        string? staging = null;
        try
        {
            if (string.IsNullOrWhiteSpace(info.Sha256))
            {
                // Without a published digest there is nothing to verify the download against.
                ShowToast(Loc.T("Güncelleme doğrulanamadı: GitHub bu sürüm için bir özet yayınlamamış."), false);
                OpenUrl(UpdateService.ReleasePageUrl);
                return;
            }

            var progress = new Progress<double>(fraction =>
            {
                if (TxtUpdateNow != null) TxtUpdateNow.Text = Loc.T($"Güncelleme indiriliyor… %{fraction * 100:F0}");
            });
            zip = await UpdateService.DownloadAsync(info, progress);

            if (!UpdateService.VerifyDigest(zip, info.Sha256))
            {
                ShowToast(Loc.T("Güncelleme doğrulanamadı (özet uyuşmuyor)."), false);
                return;
            }

            staging = UpdateService.ExtractToStaging(zip);
            var exe = UpdateService.InstallFromStaging(staging, AppContext.BaseDirectory);

            ShowToast(Loc.T("Güncelleme hazır; NitroLite yeniden başlatılıyor."));
            App.ReleaseSingleInstanceMutex();
            UpdateService.StartInstalled(exe);
            System.Windows.Application.Current?.Shutdown();
        }
        catch (UnauthorizedAccessException)
        {
            ShowToast(Loc.T("Klasöre yazılamıyor; güncellemeyi elle indirin."), false);
            OpenUrl(UpdateService.ReleasePageUrl);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Güncelleme kurulamadı: {ex.Message}"), false);
        }
        finally
        {
            UpdateService.CleanUp(staging, zip);
            _updateBusy = false;
            if (BtnUpdateNow != null)
            {
                BtnUpdateNow.IsEnabled = true;
                if (TxtUpdateNow != null && _pendingUpdate != null)
                    TxtUpdateNow.Text = Loc.T($"Güncelle {_pendingUpdate.DisplayVersion}");
            }
        }
    }

    /// <summary>
    /// Opens a prefilled GitHub issue. Only the version, the Windows build, the laptop model and the language go in;
    /// no user name, no file path, no log content.
    /// </summary>
    private void BtnReportBug_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var language = Loc.Languages.FirstOrDefault(l => l.Code == Loc.Language)?.NativeName ?? Loc.Language;
            var body = UpdateService.BuildIssueBody(
                UpdateService.CurrentVersionText,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                UpdateService.GetMachineModel(),
                language);
            OpenUrl(UpdateService.BuildIssueUrl(Loc.T("NitroLite hata bildirimi"), body));
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Hata bildirimi açılamadı: {ex.Message}"), false);
        }
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Günlük klasörü açılamadı: {ex.Message}"), false);
        }
    }

    private void LinkAuthor_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/canerztk8");

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T($"Bağlantı açılamadı: {ex.Message}"), false);
        }
    }
}

