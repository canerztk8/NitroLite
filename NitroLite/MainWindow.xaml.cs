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
    private readonly ThermalHealthService _thermalService;
    private readonly StorageHealthService _storageService;
    private readonly IDisplayRefreshRateService _displayService;
    private readonly FpsMonitorService _fpsMonitorService;
    private AtkMouseService _atkMouseService;
    private PowerAutomationService? _powerAutomationService;

    private readonly DispatcherTimer _pollTimer = new();
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ToolStripMenuItem? _menuItemRetryDesktop;
    private OsdOverlayWindow? _osdWindow;
    private bool _isOsdOpen = false;
    private readonly List<MonitorData> _telemetryHistory = new();
    private string _selectedChartMetric = "CPU_TEMP";
    private DispatcherTimer? _toastTimer;
    private long _telemetrySampleIndex = 0;

    private OperatingMode _currentMode = OperatingMode.Default;
    private OperatingMode _savedModeBeforeGame = OperatingMode.Default;
    private CpuBoostMode _savedCpuBoostBeforeGame = CpuBoostMode.Aggressive;
    private FanMode _currentFanMode = FanMode.Auto;
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

    public bool HasPendingDesktopRestore => _hasPendingDesktopRestore || _pendingModeRestore || _pendingCpuBoostRestore || _pendingFanRestore || _pendingMouseRestore || (_powerAutomationService?.HasPendingAcRestore == true);
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
    private int _selectedTrendDays = 30;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

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
        _thermalService = new();
        _storageService = new();
        _displayService = new DisplayRefreshRateService();
        _fpsMonitorService = new();
        _atkMouseService = new();

        InitializeComponent();
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
        _thermalService = new ThermalHealthService();
        _storageService = new StorageHealthService();
        _displayService = displayService ?? (isTestHarness ? new TestDisplayRefreshRateServiceStub() : new DisplayRefreshRateService());
        _fpsMonitorService = new FpsMonitorService();
        if (settings != null) _appSettings = settings;
        if (mouseProfileSwitcher != null) MouseProfileSwitcher = mouseProfileSwitcher;

        if (!isTestHarness)
        {
            InitializeComponent();
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
        }
        catch { }

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = appIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "NitroLite"
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Göster / Aç", null, (s, e) => ShowWindow());

        _menuItemRetryDesktop = new System.Windows.Forms.ToolStripMenuItem("Masaüstü ayarlarını yeniden dene", null, async (s, e) =>
        {
            await RetryPendingDesktopRestoreAsync();
        });
        _menuItemRetryDesktop.Enabled = false;
        menu.Items.Add(_menuItemRetryDesktop);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        menu.Items.Add("Sessiz Mod (Silent)", null, async (s, e) => await SetManualModeAsync(OperatingMode.Quiet));
        menu.Items.Add("Dengeli Mod (Balanced)", null, async (s, e) => await SetManualModeAsync(OperatingMode.Default));
        menu.Items.Add("Performans Modu (Power)", null, async (s, e) => await SetManualModeAsync(OperatingMode.Extreme));
        menu.Items.Add("Turbo Mod", null, async (s, e) => await SetManualModeAsync(OperatingMode.Turbo));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("CPU Boost: Devre Dışı", null, async (s, e) => await SetManualCpuBoostAsync(CpuBoostMode.Disabled));
        menu.Items.Add("CPU Boost: Agresif (5.0 GHz)", null, async (s, e) => await SetManualCpuBoostAsync(CpuBoostMode.Aggressive));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (s, e) => RealExit());

        menu.Opening += (s, e) =>
        {
            UpdateTrayRetryMenuItemState();
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
            await EnsureInitializedAsync();
        };

        StateChanged += (s, e) => UpdateTelemetryPollingRate();
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
        // When minimized in tray, throttle polling to 3000ms to reduce background CPU cycles & power
        var targetInterval = isHiddenOrMinimized ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(1);
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
                try
                {
                    _currentMode = recovery.SavedMode;
                    await _agentClient.SetOperatingModeAsync(recovery.SavedMode);
                    await _cpuPowerService.SetBoostModeAsync(recovery.SavedBoost);
                    await _agentClient.SetFanModeAsync(recovery.SavedFan);
                    if (_appSettings.AtkMouseIntegrationEnabled && recovery.SavedMouseProfile >= 1 && recovery.SavedMouseProfile <= 4)
                    {
                        await SwitchMouseProfileInternalAsync(recovery.SavedMouseProfile);
                    }
                    ClearGameSessionRecoveryState();
                    ShowToast("Önceki oyun oturumu beklenmedik şekilde kapandı; masaüstü ayarları geri yüklendi.");
                }
                catch { }
            }

            // 1. Initial Mode
            var initialMode = await _agentClient.TryGetOperatingModeAsync();
            if (initialMode.HasValue)
            {
                UpdateModeUi(initialMode.Value);
            }
            else
            {
                // Do not claim "Dengeli" when the service did not answer; no card is highlighted.
                ShowUnknownModeUi();
            }

            // 1.1 Initial GPU Mode (MUX Switch)
            await RefreshGpuModeAsync();

            // 2. Initial Lighting (Check settings.json, then active_rgb_state.json)
            bool restoredSavedRgb = false;

            if (_appSettings.IsBacklightOff || (_appSettings.KeyboardLighting != null && (_appSettings.KeyboardLighting.Brightness == 0 || _appSettings.KeyboardLighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase))))
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
            InitializeDefaultMouseProfileUi();

            // 6. Battery Health Details, Thermal Paste / Fan & Storage Health
            await RefreshBatteryHealthAsync();
            RefreshThermalHealthUi();
            await RefreshStorageHealthUi();

            // Eco baseline left behind by a crash or exit on battery: load it so the evaluation below
            // restores the desktop settings when running on the adapter.
            if (_powerAutomationService!.RestorePersistedEcoState())
            {
                System.Diagnostics.Trace.WriteLine("Recovered persisted battery Eco baseline.");
            }
            await _powerAutomationService.EvaluatePowerStateAsync();

            // 7. Debloat & NitroSense Suppression check
            _debloatService.DisableNitroSenseBackgroundExecution();
            RefreshServiceList();
            ChkAutoStart.IsChecked = _debloatService.IsNitroLiteAutoStartEnabled();
        }
        finally
        {
            _isInitializingUi = false;
        }
    }

    private void LoadSettings()
    {
        var loadedSettings = SettingsManager.LoadSettingsWithFallback(AppSettingsConfigFile, AppSettingsConfigFile + ".bak", LegacyConfigFile);
        foreach (var property in typeof(AppSettings).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(_appSettings, property.GetValue(loadedSettings));

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

        if (_appSettings.IsBacklightOff || (_appSettings.KeyboardLighting != null && (_appSettings.KeyboardLighting.Brightness == 0 || _appSettings.KeyboardLighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase))))
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
        ChkAutoRamClean.IsChecked = _appSettings.AutoRamCleanerEnabled;

        // Persisted toggles: apply to the services directly (the Changed handlers ignore events
        // raised while the window is not loaded / still initializing).
        _gameService.AutoGameModeEnabled = _appSettings.AutoGameModeEnabled;
        if (ChkAutoGameMode != null) ChkAutoGameMode.IsChecked = _appSettings.AutoGameModeEnabled;
        if (TxtGameStatus != null && !_appSettings.AutoGameModeEnabled)
        {
            TxtGameStatus.Text = "Durum: Devre Dışı (Manuel Kontrol)";
        }
        _batteryService.WatchdogEnabled = _appSettings.BatteryWatchdogEnabled;
        if (ChkWatchdog != null) ChkWatchdog.IsChecked = _appSettings.BatteryWatchdogEnabled;

        _selectedChartMetric = string.IsNullOrEmpty(_appSettings.SelectedChartMetric) ? "CPU_TEMP" : _appSettings.SelectedChartMetric;
        SetComboBoxByTag(CmbChartMetric, _selectedChartMetric);

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
        if (WindowState == WindowState.Normal)
        {
            _appSettings.WindowLeft = Left;
            _appSettings.WindowTop = Top;
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
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
            catch { }
        }
        catch { }
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
        }
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
        {
            _ = ReassertKeyboardLightingAsync();
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

    public void ShowToast(string message, bool isSuccess = true)
    {
        ToastHandler?.Invoke(message, isSuccess);
        if (Dispatcher.HasShutdownStarted) return;

        Action action = () =>
        {
            if (Dispatcher.HasShutdownStarted || ToastBanner == null || TxtToastIcon == null || TxtToastMessage == null) return;

            TxtToastIcon.Text = isSuccess ? "●" : "!";
            TxtToastIcon.Foreground = TryFindResource(isSuccess ? "AccentGreen" : "AccentCyan") as SolidColorBrush ?? (isSuccess ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Cyan);
            ToastBanner.BorderBrush = TryFindResource(isSuccess ? "AccentGreen" : "AccentCyan") as SolidColorBrush ?? (isSuccess ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Cyan);
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

    private bool? _lastServiceTelemetryAvailable;
    private const string ServiceUnavailableText = "-- (Servis yok)";

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
            ShowToast("Acer servisine (AcerAgentService) ulaşılamıyor; yalnızca Windows ve NVIDIA'dan okunabilen veriler gösteriliyor.", false);
        }
        else if (previous == false)
        {
            ShowToast("Acer servisi bağlantısı geri geldi; tüm sensörler okunuyor.");
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
                _thermalService.RecordSample(data);
            }
            _telemetrySampleIndex++;

            bool isWindowVisible = IsVisible && WindowState != WindowState.Minimized;
            bool isGpuSleeping = data.GpuTemperature <= 0;

            if (isWindowVisible)
            {
                // Titlebar / Sidebar
                TxtSideCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
                TxtSideGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping);
                TxtSideCpuFan.Text = TelemetryFormatter.FormatFanSpeed(data.CpuFanSpeed);

                // Sub-Component Thermal Matrix: Motherboard / VRM (SYS1_TEMPERATURE)
                if (TxtSubMoboTemp != null)
                    TxtSubMoboTemp.Text = TelemetryFormatter.FormatTemperature(data.SysTemperature > 0 ? data.SysTemperature : null);

                // Tab 1: Fans
                TxtCpuFanRpm.Text = TelemetryFormatter.FormatFanSpeed(data.CpuFanSpeed);
                TxtGpuFanRpm.Text = TelemetryFormatter.FormatFanSpeed(data.GpuFanSpeed);
                BarCpuFan.Value = data.CpuFanSpeed;
                BarGpuFan.Value = data.GpuFanSpeed;

                // Tab 2: Monitoring - CPU
                TxtCpuCoreCount.Text = $" ({data.CpuCores} Çekirdek)";
                TxtMonCpuTemp.Text = TelemetryFormatter.FormatCpuTemperature(data.CpuTemperature);
                BarMonCpuTemp.Value = data.CpuTemperature;

                TxtMonCpuPower.Text = TelemetryFormatter.FormatCpuPower(data.CpuPowerWatts) + " (tahmini)";
                BarMonCpuPower.Value = data.CpuPowerWatts;

                TxtMonCpuUsage.Text = $"{data.CpuUsage:F1} %";
                BarMonCpuUsage.Value = data.CpuUsage;

                TxtMonCpuFreqGhz.Text = TelemetryFormatter.FormatCpuFrequency(data.CpuFrequencyMHz);
                BarMonCpuFreq.Value = Math.Min(data.CpuFrequencyMHz, BarMonCpuFreq.Maximum);

                // Tab 2: Monitoring - GPU
                TxtMonGpuTemp.Text = TelemetryFormatter.FormatGpuTemperature(data.GpuTemperature, isGpuSleeping);
                BarMonGpuTemp.Value = data.GpuTemperature;

                TxtMonGpuPower.Text = TelemetryFormatter.FormatGpuPower(data.GpuPowerWatts, isGpuSleeping);
                BarMonGpuPower.Value = data.GpuPowerWatts;

                TxtMonGpuUsage.Text = $"{data.GpuUsage:F1} %";
                BarMonGpuUsage.Value = data.GpuUsage;

                BarMonVram.Maximum = data.VramTotalMB > 0 ? data.VramTotalMB : 8192;
                BarMonVram.Value = Math.Min(data.VramUsedMB, BarMonVram.Maximum);
                TxtMonVram.Text = TelemetryFormatter.FormatVramUsage(data.VramUsedMB, data.VramTotalMB, isGpuSleeping);

                // RAM
                if (data.RamTotalMB > 0)
                {
                    var ramUsedGB = (data.RamTotalMB * (data.RamUsage / 100.0)) / 1024.0;
                    var ramTotalGB = data.RamTotalMB / 1024.0;
                    TxtMonRamDetail.Text = $"{ramUsedGB:F1} GB / {ramTotalGB:F1} GB";
                    TxtMonRamPercent.Text = $"{data.RamUsage:F1} %";
                    BarMonRam.Value = data.RamUsage;
                }
                else
                {
                    TxtMonRamDetail.Text = "-- GB / -- GB";
                    TxtMonRamPercent.Text = "-- %";
                    BarMonRam.Value = 0;
                }

                TxtMonSysTemp.Text = $"{data.SysTemperature} °C";

                if (isFallback)
                {
                    ApplyServiceUnavailableTelemetryText(data);
                }
            }

            // Update tray tooltip regardless of window visibility
            if (_notifyIcon != null)
            {
                var tip = isFallback
                    ? $"NitroLite: Acer servisi yanıt vermiyor | CPU: %{data.CpuUsage:F0}"
                    : $"NitroLite: {_currentMode} | CPU: {data.CpuTemperature}°C | GPU: {data.GpuTemperature}°C";
                _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
            }

            // Optimal Auto RAM Cleaner Check (32 GB RAM optimized) - suppressed during active gameplay to prevent micro-stutters
            bool isGameActive = _isGameSessionActive || _gameService.IsGameActive;
            if (_appSettings.AutoRamCleanerEnabled && !isGameActive && _memoryService.ShouldAutoClean(data.RamUsage, data.RamTotalMB, isGameActive))
            {
                var res = await _memoryService.CleanMemoryAsync();
                if (res.FreedMegabytes > 150)
                {
                    string freedStr = res.FreedMegabytes >= 1024 ? $"{res.FreedMegabytes / 1024.0:F1} GB" : $"{res.FreedMegabytes:F0} MB";
                    ShowToast($"Bellek temizlendi: {freedStr} alan boşaltıldı.");
                    if (TxtRamCleanStatus != null)
                    {
                        TxtRamCleanStatus.Text = $" ({freedStr} Boşaltıldı)";
                        TxtRamCleanStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                    }
                }
            }

            if (!isFallback)
            {
                // Update OSD if open
                if (_isOsdOpen && _osdWindow != null && _osdWindow.IsVisible)
                {
                    _osdWindow.UpdateData(data, _currentMode);
                }

                // Keep rolling 60 seconds history
                _telemetryHistory.Add(data);
                if (_telemetryHistory.Count > 60)
                {
                    _telemetryHistory.RemoveAt(0);
                }

                RenderTelemetryChart();
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
                    else if (_gameService.GetProfile(activeGame.ExeName) != null)
                    {
                        // The active game is not in the displayed snapshot (list changed underneath); rebuild it.
                        PopulateConfiguredGames(activeGame.ExeName);
                    }
                }
            }

            // Periodically refresh thermal health card if Battery & Health tab is active (every 5 samples)
            if (TabBattery?.Visibility == Visibility.Visible && (_telemetrySampleIndex % 5 == 0))
            {
                RefreshThermalHealthUi();
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

    private void RenderTelemetryChart()
    {
        if (_telemetryHistory.Count < 2 || CanvasHistoryChart == null) return;
        if (!IsVisible || WindowState == WindowState.Minimized || TabMonitoring?.Visibility != Visibility.Visible) return;

        double width = CanvasHistoryChart.ActualWidth;
        double height = CanvasHistoryChart.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            width = 720;
            height = 140;
        }

        List<double> values = new();
        foreach (var d in _telemetryHistory)
        {
            double val = _selectedChartMetric switch
            {
                "CPU_TEMP" => d.CpuTemperature,
                "GPU_TEMP" => d.GpuTemperature,
                "CPU_WATT" => d.CpuPowerWatts,
                "GPU_WATT" => d.GpuPowerWatts,
                "CPU_GHZ" => d.CpuFrequencyMHz / 1000.0,
                "CPU_USAGE" => d.CpuUsage,
                "GPU_USAGE" => d.GpuUsage,
                "RAM_USAGE" => d.RamUsage,
                "VRAM_GB" => d.VramUsedMB / 1024.0,
                _ => d.CpuTemperature
            };
            values.Add(val);
        }

        string unit = _selectedChartMetric switch
        {
            "CPU_TEMP" or "GPU_TEMP" => "°C",
            "CPU_WATT" or "GPU_WATT" => "W",
            "CPU_GHZ" => "GHz",
            "CPU_USAGE" or "GPU_USAGE" or "RAM_USAGE" => "%",
            "VRAM_GB" => "GB",
            _ => ""
        };

        double current = values.Last();
        double min = values.Min();
        double max = values.Max();
        double avg = values.Average();

        bool isFine = _selectedChartMetric is "CPU_GHZ" or "VRAM_GB";
        TxtChartCurrentVal.Text = isFine ? $"{current:F2} {unit}" : $"{current:F1} {unit}";
        TxtChartMinVal.Text = isFine ? $"{min:F2} {unit}" : $"{min:F1} {unit}";
        TxtChartMaxVal.Text = isFine ? $"{max:F2} {unit}" : $"{max:F1} {unit}";
        TxtChartAvgVal.Text = isFine ? $"{avg:F2} {unit}" : $"{avg:F1} {unit}";

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
            TxtChartGridTop.Text = isFine ? $"{rangeMax:F1} {unit}" : $"{(int)rangeMax} {unit}";
        if (TxtChartGridMid != null)
            TxtChartGridMid.Text = isFine ? $"{((rangeMin + rangeMax) / 2.0):F1} {unit}" : $"{(int)((rangeMin + rangeMax) / 2.0)} {unit}";
        if (TxtChartGridBottom != null)
            TxtChartGridBottom.Text = isFine ? $"{rangeMin:F1} {unit}" : $"{(int)rangeMin} {unit}";

        double range = rangeMax - rangeMin;
        if (range <= 0) range = 1;

        int count = values.Count;
        double stepX = width / Math.Max(count - 1, 1);

        var linePoints = new PointCollection();
        var areaPoints = new PointCollection();

        areaPoints.Add(new WpfPoint(0, height));

        double lastX = 0;
        double lastY = height / 2;

        for (int i = 0; i < count; i++)
        {
            double x = i * stepX;
            double normalized = Math.Clamp((values[i] - rangeMin) / range, 0.0, 1.0);
            double y = height - (normalized * (height - 24)) - 12;

            var pt = new WpfPoint(x, y);
            linePoints.Add(pt);
            areaPoints.Add(pt);

            lastX = x;
            lastY = y;
        }

        areaPoints.Add(new WpfPoint((count - 1) * stepX, height));

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
        }
        _osdWindow.CycleCorner();
        _appSettings.OsdCorner = _osdWindow.CurrentCorner;
        SaveSettings();

        string cornerDesc = _osdWindow.CurrentCorner switch
        {
            0 => "Sol Üst",
            1 => "Sağ Üst",
            2 => "Sağ Alt",
            3 => "Sol Alt",
            _ => "Sol Üst"
        };
        ShowToast($"OSD konumu değiştirildi: {cornerDesc}");
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
            _osdWindow.Show();
            BtnToggleOsd.Background = (SolidColorBrush)FindResource("AccentCyan");
            BtnToggleOsd.Foreground = System.Windows.Media.Brushes.Black;
            BtnToggleOsd.Content = "OSD Katmanı: Açık (Kapat)";

            if (showToast) ShowToast("Ekran üstü gösterge etkinleştirildi.");
        }
        else
        {
            _osdWindow?.Hide();
            BtnToggleOsd.Background = new BrushConverter().ConvertFromString("#162436") as WpfBrush;
            BtnToggleOsd.Foreground = (SolidColorBrush)FindResource("AccentCyan");
            BtnToggleOsd.Content = "Ekran Üstü Gösterge (OSD)";
            if (showToast) ShowToast("Ekran üstü gösterge kapatıldı.");
        }
    }

    // ==========================================
    // CPU Boost (Module B)
    // ==========================================

    private void UpdateBoostUi(CpuBoostMode mode)
    {
        if (TxtBoostStatusBadge != null)
        {
            switch (mode)
            {
                case CpuBoostMode.Disabled:
                    TxtBoostStatusBadge.Text = " (Devre Dışı)";
                    TxtBoostStatusBadge.Foreground = TryFindResource("AccentGreen") as SolidColorBrush ?? System.Windows.Media.Brushes.Green;
                    break;
                case CpuBoostMode.Efficient:
                    TxtBoostStatusBadge.Text = " (Dengeli)";
                    TxtBoostStatusBadge.Foreground = TryFindResource("AccentCyan") as SolidColorBrush ?? System.Windows.Media.Brushes.Cyan;
                    break;
                case CpuBoostMode.Aggressive:
                default:
                    TxtBoostStatusBadge.Text = " (Agresif / 5.0 GHz)";
                    TxtBoostStatusBadge.Foreground = TryFindResource("AccentRed") as SolidColorBrush ?? System.Windows.Media.Brushes.Red;
                    break;
            }
        }

        if (TxtSideCpuBoost != null)
        {
            switch (mode)
            {
                case CpuBoostMode.Disabled:
                    TxtSideCpuBoost.Text = "Devre Dışı";
                    TxtSideCpuBoost.Foreground = TryFindResource("AccentGreen") as SolidColorBrush ?? System.Windows.Media.Brushes.Green;
                    break;
                case CpuBoostMode.Efficient:
                    TxtSideCpuBoost.Text = "Dengeli";
                    TxtSideCpuBoost.Foreground = TryFindResource("AccentCyan") as SolidColorBrush ?? System.Windows.Media.Brushes.Cyan;
                    break;
                case CpuBoostMode.Aggressive:
                default:
                    TxtSideCpuBoost.Text = "Agresif";
                    TxtSideCpuBoost.Foreground = TryFindResource("AccentRed") as SolidColorBrush ?? System.Windows.Media.Brushes.Red;
                    break;
            }
        }
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

        _hasPendingDesktopRestore = HasPendingDesktopRestore;
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
            ShowToast($"CPU modu uygulanamadı: {ex.Message}", false);
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
            return await ApplyCpuBoostModeAsync(target);
        }

        bool acOk = await _cpuPowerService.SetBoostModeAsync(_savedCpuBoostBeforeGame, targetAc: true, targetDc: false);
        bool dcOk = await _cpuPowerService.SetBoostModeAsync(CpuBoostMode.Disabled, targetAc: false, targetDc: true);
        if (acOk && dcOk)
        {
            UpdateBoostUi(_cpuPowerService.CurrentMode);
            return true;
        }
        ShowToast("CPU boost modu geri yüklenemedi.", false);
        return false;
    }

    private async Task<bool> ApplyCpuBoostModeAsync(CpuBoostMode mode)
    {
        var ok = await _cpuPowerService.SetBoostModeAsync(mode);
        if (ok)
        {
            UpdateBoostUi(mode);
            ShowToast($"CPU boost modu: {mode}");
            return true;
        }
        else
        {
            ShowToast($"CPU boost modu uygulanamadı: {mode}", false);
            return false;
        }
    }

    // ==========================================
    // GPU Operating Mode (MUX Switch) Control
    // ==========================================

    private int? _supportedGpuCapability;
    private bool? _gpuModeSwitchSupported;

    public static bool IsGpuModeSupported(int capabilityBitmask, GpuOperatingMode mode)
    {
        int mask = 1 << (int)mode;
        return (capabilityBitmask & mask) != 0;
    }

    /// <summary>
    /// Mirrors NitroSense: GPU mode switching is offered only when AASSvc answers SUPPORT_GPU_MODE with data[0] == 1
    /// AND reports a capability bitmask that contains the target mode. Unknown answers never allow a switch.
    /// </summary>
    public static bool CanSwitchGpuMode(bool? serviceSupportsGpuMode, int? capabilityBitmask, GpuOperatingMode target)
    {
        return serviceSupportsGpuMode == true
            && capabilityBitmask.HasValue
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
            var support = await _agentClient.GetSupportGpuModeAsync();
            _gpuModeSwitchSupported = support;
            var cap = await _agentClient.GetGpuModeCapabilityAsync();
            _supportedGpuCapability = cap;

            if (CmbGpuMode != null)
            {
                CmbGpuMode.Items.Clear();
            }

            if (support == true && cap.HasValue && cap.Value > 0 && CmbGpuMode != null)
            {

                // Bit 2 (4): Dynamic Display Switch (DDS / Advanced Optimus)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.Automatic))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = "Otomatik Seçim (DDS / Advanced Optimus)",
                        Tag = "Automatic"
                    });
                }

                // Bit 0 (1): Optimus (Hybrid iGPU + dGPU)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.Optimus))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = "Optimus (Hibrit iGPU + dGPU)",
                        Tag = "Optimus"
                    });
                }

                // Bit 1 (2): Discrete Only (dGPU)
                if (IsGpuModeSupported(cap.Value, GpuOperatingMode.DiscreteGpu))
                {
                    CmbGpuMode.Items.Add(new ComboBoxItem
                    {
                        Content = "Yalnızca Harici GPU (NVIDIA dGPU)",
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
                    TxtGpuModeBadge.Text = "Bilinmiyor";
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("TextMuted");
                }
            }

            if (CmbGpuMode != null && CmbGpuMode.SelectedIndex == -1 && CmbGpuMode.Items.Count > 0)
            {
                CmbGpuMode.SelectedIndex = 0;
            }

            if (support == true && cap.HasValue && cap.Value > 0 && CmbGpuMode?.Items.Count > 0)
            {
                SetGpuModeControlsEnabled(true, null);
            }
            else if (support == false)
            {
                SetGpuModeControlsEnabled(false,
                    "Acer servisi bu cihazda GPU modu değiştirmeyi şu an desteklenmiyor olarak bildiriyor (NitroSense de bu durumda seçeneği gizler). Modu BIOS'tan (F2 > Display Mode) değiştirebilirsiniz.");
            }
            else
            {
                SetGpuModeControlsEnabled(false,
                    "GPU modu desteği Acer servisinden doğrulanamadı; güvenlik için mod değiştirme kapalı.");
            }
        }
        catch (Exception ex)
        {
            _currentGpuMode = null;
            _gpuModeSwitchSupported = null;
            SetGpuModeControlsEnabled(false, "GPU modu desteği Acer servisinden doğrulanamadı; güvenlik için mod değiştirme kapalı.");
            if (TxtGpuModeBadge != null)
            {
                TxtGpuModeBadge.Text = "Bilinmiyor";
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
                    TxtGpuModeBadge.Text = "NVIDIA GPU Yalnızca (dGPU)";
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("AccentRed");
                    SelectGpuModeCombo("DiscreteGpu");
                    break;
                case GpuOperatingMode.Optimus:
                    TxtGpuModeBadge.Text = "Optimus (Hibrit iGPU + dGPU)";
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                    SelectGpuModeCombo("Optimus");
                    break;
                case GpuOperatingMode.Automatic:
                default:
                    TxtGpuModeBadge.Text = "Otomatik Seçim (DDS / Dynamic)";
                    TxtGpuModeBadge.Foreground = (SolidColorBrush)FindResource("AccentCyan");
                    SelectGpuModeCombo("Automatic");
                    break;
            }
        }
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
            ShowToast("Lütfen bir GPU çalışma modu seçin.", false);
            return;
        }

        if (!Enum.TryParse<GpuOperatingMode>(tagStr, out var targetMode))
        {
            ShowToast("Geçersiz GPU modu.", false);
            return;
        }

        if (!CanSwitchGpuMode(_gpuModeSwitchSupported, _supportedGpuCapability, targetMode))
        {
            ShowToast("Seçilen GPU modu Acer servisi tarafından desteklenmiyor veya destek doğrulanamadı.", false);
            return;
        }

        if (_currentGpuMode.HasValue && targetMode == _currentGpuMode.Value)
        {
            ShowToast("Seçilen GPU modu zaten aktif.");
            return;
        }

        string modeDisplayName = targetMode switch
        {
            GpuOperatingMode.DiscreteGpu => "Yalnızca Harici GPU (NVIDIA dGPU)",
            GpuOperatingMode.Optimus => "Optimus (Hibrit iGPU + dGPU)",
            _ => "Otomatik Seçim (DDS / Advanced Optimus)"
        };

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"GPU Çalışma Modunu '{modeDisplayName}' olarak ayarlamak üzeresiniz.\n\n" +
            "Donanım MUX anahtarı değiştirilirken ekran anlık olarak kararıp geri gelebilir ve yeni donanım yapılandırmasının tam devreye girmesi için bilgisayarınızı yeniden başlatmanız önerilir.\n\n" +
            "Devam etmek istiyor musunuz?",
            "GPU Çalışma Modu (MUX Switch)",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnApplyGpuMode.IsEnabled = false;
        try
        {
            var success = await _agentClient.SetGpuModeAsync(targetMode);
            if (success)
            {
                UpdateGpuModeUi(targetMode);
                ShowToast($"GPU modu '{modeDisplayName}' olarak güncellendi! Tam geçerlilik için yeniden başlatma önerilir.");
            }
            else
            {
                ShowToast("GPU modu uygulanamadı. Acer Agent Service yanıt vermedi.", false);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Hata: {ex.Message}", false);
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

    private string? GetSelectedGameExeName()
    {
        if (CmbConfiguredGames == null) return null;
        int idx = CmbConfiguredGames.SelectedIndex;
        var snapshot = _configuredGameExeNames;
        return idx >= 0 && idx < snapshot.Count ? snapshot[idx] : null;
    }

    private GameProfile? GetSelectedGameProfile()
    {
        var exe = GetSelectedGameExeName();
        return exe == null ? null : _gameService.GetProfile(exe);
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
            string.Equals(p.DisplayName, p.ExeName, StringComparison.OrdinalIgnoreCase)
                ? p.DisplayName
                : $"{p.DisplayName} ({p.ExeName})"
        ).ToList();

        _isUpdatingGameUi = true;
        try
        {
            _configuredGameExeNames = profiles.Select(p => p.ExeName).ToList();
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
            ? $" [{profile.DisplayName} - ŞU AN OYNANIYOR]" 
            : $" [{profile.DisplayName}]";
        TxtSelectedGameNameBadge.Foreground = (SolidColorBrush)FindResource(isCurrentlyActive ? "AccentGreen" : "TextSecondary");
        TxtSelectedGameExePath.Text = string.IsNullOrEmpty(profile.InstallPath) 
            ? $"{profile.ExeName}.exe" 
            : (profile.InstallPath.Length > 48 ? "..." + profile.InstallPath[^45..] : profile.InstallPath);

        // 1. Target Operating Mode ComboBox
        SetComboBoxByTag(CmbGameTargetMode, profile.TargetMode.ToString());

        // 2. Target CPU Boost ComboBox
        SetComboBoxByTag(CmbGameTargetCpuBoost, profile.TargetCpuBoost.ToString());

        // 3. Target Fan Mode ComboBox
        SetComboBoxByTag(CmbGameTargetFan, profile.TargetFanMode.ToString());

        // 4. Target ATK Mouse Profile ComboBox
        if (CmbGameAtkMouseProfile != null)
        {
            var targetIdx = profile.TargetAtkMouseProfile;
            CmbGameAtkMouseProfile.SelectedIndex = (targetIdx >= 0 && targetIdx < CmbGameAtkMouseProfile.Items.Count) 
                ? targetIdx 
                : 0;
        }
    }

    private async Task<bool> SwitchMouseProfileInternalAsync(int targetProfile)
    {
        if (MouseProfileSwitcher != null)
        {
            return await Task.Run(() => MouseProfileSwitcher(targetProfile));
        }
        return await Task.Run(() => _atkMouseService.SwitchProfile(targetProfile));
    }

    private async void BtnTestMouseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbGameAtkMouseProfile == null) return;
        var selectedIdx = CmbGameAtkMouseProfile.SelectedIndex;
        if (selectedIdx <= 0)
        {
            ShowToast("Test için 1-4 arasında bir ATK fare profili seçin.", false);
            return;
        }

        if (BtnTestMouseProfile != null) BtnTestMouseProfile.IsEnabled = false;
        try
        {
            bool success = await SwitchMouseProfileInternalAsync(selectedIdx);
            if (success)
            {
                if (!_isGameSessionActive)
                {
                    _savedMouseProfileBeforeGame = selectedIdx;
                }
                UpdateSideMouseProfileStatus(selectedIdx);
                ShowToast($"ATK A9 Fare donanımı Config {selectedIdx} profiline geçirildi.");
            }
            else
            {
                ShowToast("ATK A9 fareye erişilemedi veya alıcı yanıt vermedi.", false);
            }
        }
        finally
        {
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
        UpdateDefaultMouseProfileBadge(profile);

        if (_appSettings.AtkMouseIntegrationEnabled)
        {
            UpdateSideMouseProfileStatus(profile >= 1 && profile <= 4 ? profile : _atkMouseService.CurrentActiveProfile);

            // Apply on startup if set to a specific profile (1..4) and no game is currently active
            if (profile >= 1 && profile <= 4 && !_isGameSessionActive)
            {
                _ = Task.Run(async () =>
                {
                    bool ok = await SwitchMouseProfileInternalAsync(profile);
                    if (ok)
                    {
                        if (!_isGameSessionActive)
                        {
                            _savedMouseProfileBeforeGame = profile;
                        }
                        await Dispatcher.InvokeAsync(() => UpdateSideMouseProfileStatus(profile));
                    }
                });
            }
        }
    }

    private void UpdateSideMouseProfileStatus(int profile)
    {
        if (TxtSideMouseProfile == null) return;
        if (profile >= 1 && profile <= 4)
        {
            TxtSideMouseProfile.Text = $"Config {profile}";
            TxtSideMouseProfile.Foreground = (SolidColorBrush)FindResource("AccentGreen");
        }
        else
        {
            TxtSideMouseProfile.Text = "Bilinmiyor";
            TxtSideMouseProfile.Foreground = (SolidColorBrush)FindResource("TextSecondary");
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
            UpdateSideMouseProfileStatus(_atkMouseService.CurrentActiveProfile);
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
            ShowToast("ATK Fare Entegrasyonu etkinleştirildi.");
        }
        else
        {
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
                            ShowToast($"ATK Fare Entegrasyonu devre dışı bırakıldı (Fare Config {targetMouseProfile} profiline geri döndürüldü).");
                        }
                        else
                        {
                            _pendingMouseRestore = true;
                            ShowToast("ATK Fare Entegrasyonu devre dışı bırakıldı (Masaüstü fare profiline geri dönülemedi).", false);
                        }
                    }
                }
                else
                {
                    ShowToast("ATK Fare Entegrasyonu devre dışı bırakıldı.");
                }
            }
            finally
            {
                _gameProfileTransitionLock.Release();
            }
        }
    }

    private void UpdateDefaultMouseProfileBadge(int profile)
    {
        if (TxtDefaultMouseProfileStatus == null) return;
        if (profile >= 1 && profile <= 4)
        {
            TxtDefaultMouseProfileStatus.Text = $"Config {profile} (Aktif)";
            TxtDefaultMouseProfileStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
        }
        else
        {
            TxtDefaultMouseProfileStatus.Text = "Son Kullanılan (Pasif)";
            TxtDefaultMouseProfileStatus.Foreground = (SolidColorBrush)FindResource("TextSecondary");
        }
    }

    private void CmbDefaultAtkMouseProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi || CmbDefaultAtkMouseProfile == null) return;
        if (CmbDefaultAtkMouseProfile.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int val))
        {
            _appSettings.DefaultAtkMouseProfile = val;
            UpdateDefaultMouseProfileBadge(val);
            SaveSettings();
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
        SaveSettings();

        if (val >= 1 && val <= 4)
        {
            if (BtnApplyDefaultMouseProfile != null) BtnApplyDefaultMouseProfile.IsEnabled = false;
            try
            {
                bool ok = await SwitchMouseProfileInternalAsync(val);
                if (ok)
                {
                    if (!_isGameSessionActive)
                    {
                        _savedMouseProfileBeforeGame = val;
                    }
                    UpdateDefaultMouseProfileBadge(val);
                    UpdateSideMouseProfileStatus(val);
                    ShowToast($"Varsayılan fare profili Config {val} olarak ayarlandı ve donanıma uygulandı.");
                }
                else
                {
                    if (TxtDefaultMouseProfileStatus != null)
                    {
                        TxtDefaultMouseProfileStatus.Text = $"Config {val} (Ulaşılamadı)";
                        TxtDefaultMouseProfileStatus.Foreground = TryFindResource("AccentRed") as SolidColorBrush ?? System.Windows.Media.Brushes.Red;
                    }
                    ShowToast($"Varsayılan profil Config {val} kaydedildi (Fare alıcısına ulaşılamadı).", false);
                }
            }
            finally
            {
                if (BtnApplyDefaultMouseProfile != null) BtnApplyDefaultMouseProfile.IsEnabled = true;
            }
        }
        else
        {
            UpdateDefaultMouseProfileBadge(val);
            ShowToast("Varsayılan fare profili otomatik koruma (pasif) olarak kaydedildi.");
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
                ShowToast("Seçili oyun profili bulunamadı; liste yenilendi.", false);
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

            // 4. ATK Mouse Profile
            if (CmbGameAtkMouseProfile != null && CmbGameAtkMouseProfile.SelectedIndex >= 0)
            {
                profile.TargetAtkMouseProfile = CmbGameAtkMouseProfile.SelectedIndex;
            }

            _gameService.SaveProfile(profile);

            // If this game is currently running right now, hot-apply settings immediately in real-time!
            bool isCurrentlyRunning = _gameService.ActiveGameProfile != null && 
                                      _gameService.ActiveGameProfile.ExeName.Equals(profile.ExeName, StringComparison.OrdinalIgnoreCase);

            if (isCurrentlyRunning)
            {
                await ApplyLiveGameProfileAsync(profile);
            }
            else
            {
                ShowToast($"'{profile.DisplayName}' oyun profili kaydedildi.");
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Profil kaydedilemedi: {ex.Message}", false);
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

            bool modeOk = await SwitchModeAsync(profile.TargetMode);
            if (saveGen != _gameSessionGeneration || !_isGameSessionActive) return false;

            bool boostOk = await ApplyCpuBoostModeAsync(profile.TargetCpuBoost);
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
                mouseOk = await SwitchMouseProfileInternalAsync(profile.TargetAtkMouseProfile);
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

            var boostName = profile.TargetCpuBoost switch
            {
                CpuBoostMode.Disabled => "Devre Dışı",
                CpuBoostMode.Efficient => "Dengeli",
                _ => "Agresif (5.0 GHz)"
            };
            var mouseInfo = (_isMouseProfileSwitchedForGame && profile.TargetAtkMouseProfile > 0) ? $" / ATK Fare: Config {profile.TargetAtkMouseProfile}" : "";

            bool allCoreOk = modeOk && boostOk && fanOk && mouseOk;
            if (TxtGameStatus != null)
            {
                if (allCoreOk)
                {
                    TxtGameStatus.Text = $"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})";
                    if (TryFindResource("AccentGreen") is SolidColorBrush brush)
                    {
                        TxtGameStatus.Foreground = brush;
                    }
                }
                else
                {
                    TxtGameStatus.Text = $"Kısmi Aktif: {profile.DisplayName.ToUpperInvariant()} (Bazı ayarlar uygulanamadı)";
                    if (TryFindResource("AccentRed") is SolidColorBrush brush)
                    {
                        TxtGameStatus.Foreground = brush;
                    }
                }
            }

            if (allCoreOk)
            {
                ShowToast($"'{profile.DisplayName}' profili kaydedildi ve uygulandı.");
            }
            else
            {
                ShowToast($"'{profile.DisplayName}' profili kaydedildi ancak bazı donanım ayarları uygulanamadı.", false);
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
        var found = await Task.Run(() => _gameService.ScanAndAddInstalledGames());
        PopulateConfiguredGames();
        ShowToast(found > 0 ? $"{found} yeni oyun profili eklendi." : "Yeni yüklü oyun tespit edilmedi.");
    }

    private void BtnAddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Çalıştırılabilir Oyun Dosyasını Seçin",
            Filter = "Çalıştırılabilir Oyun Dosyaları (*.exe)|*.exe|Tüm Dosyalar (*.*)|*.*",
            InitialDirectory = @"C:\Program Files (x86)\Steam\steamapps\common"
        };

        if (dlg.ShowDialog(this) == true)
        {
            var exeName = Path.GetFileNameWithoutExtension(dlg.FileName);
            var cleanName = exeName.Replace("_", " ").Replace("-", " ");

            var profile = new GameProfile
            {
                ExeName = exeName,
                DisplayName = cleanName,
                InstallPath = dlg.FileName,
                TargetMode = OperatingMode.Turbo,
                TargetCpuBoost = CpuBoostMode.Disabled, // Default to serin eco
                TargetFanMode = FanMode.Auto,
                AutoApply = true
            };

            _gameService.SaveProfile(profile);
            PopulateConfiguredGames(exeName);
            ShowToast($"'{cleanName}' profili eklendi.");
        }
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
            $"'{profile.DisplayName}' oyun profilini silmek istiyor musunuz?", 
            "Profili Sil", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            _gameService.DeleteProfile(profile.ExeName);
            PopulateConfiguredGames();
            ShowToast($"'{profile.DisplayName}' profili silindi.");
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

                Func<Task> work = async () =>
                {
                    if (Dispatcher.HasShutdownStarted || sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                    try
                    {
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
                                _savedCpuBoostBeforeGame = _cpuPowerService.CurrentMode;
                                _savedFanModeBeforeGame = _currentFanMode;
                            }
                            _savedMouseProfileBeforeGame = _atkMouseService.CurrentActiveProfile > 0 ? _atkMouseService.CurrentActiveProfile : 1;
                            _hasGameBaselineCaptured = true;
                        }

                        // Mark that this game session requires a desktop restore upon exit,
                        // separated from the one-time desktop baseline capture.
                        _hasPendingDesktopRestore = true;
                        _isGameSessionActive = true;
                        _batteryService.IsGameSessionActive = true;
                        UpdateTrayRetryMenuItemState();

                        SaveGameSessionRecoveryState(new GameSessionRecoveryState
                        {
                            IsActive = true,
                            SavedMode = _savedModeBeforeGame,
                            SavedBoost = _savedCpuBoostBeforeGame,
                            SavedFan = _savedFanModeBeforeGame,
                            SavedMouseProfile = _savedMouseProfileBeforeGame,
                            Timestamp = DateTime.UtcNow
                        });

                        var boostName = profile.TargetCpuBoost switch
                        {
                            CpuBoostMode.Disabled => "Devre Dışı",
                            CpuBoostMode.Efficient => "Dengeli",
                            _ => "Agresif (5.0 GHz)"
                        };

                        var mouseInfo = "";
                        bool mouseSwitchedThisTurn = false;
                        if (_appSettings.AtkMouseIntegrationEnabled && profile.TargetAtkMouseProfile > 0)
                        {
                            if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;
                            bool mouseOk = await SwitchMouseProfileInternalAsync(profile.TargetAtkMouseProfile);
                            if (mouseOk)
                            {
                                _isMouseProfileSwitchedForGame = true;
                                mouseSwitchedThisTurn = true;
                                mouseInfo = $" / Fare: Config {profile.TargetAtkMouseProfile}";
                                UpdateSideMouseProfileStatus(profile.TargetAtkMouseProfile);
                            }
                            else
                            {
                                ShowToast("Oyun fare profiline geçilemedi.", false);
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

                        // Apply target settings with generation check at each stage under transition lock
                        bool modeOk = await SwitchModeAsync(profile.TargetMode);
                        if (sessionGen != _gameSessionGeneration || !_gameService.AutoGameModeEnabled) return;

                        bool boostOk = await ApplyCpuBoostModeAsync(profile.TargetCpuBoost);
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
                                TxtGameStatus.Text = $"Aktif: {profile.DisplayName.ToUpperInvariant()} ({profile.TargetMode} / {boostName}{mouseInfo})";
                                if (TryFindResource("AccentGreen") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                            else
                            {
                                TxtGameStatus.Text = $"Kısmi Aktif: {profile.DisplayName.ToUpperInvariant()} (Bazı ayarlar uygulanamadı)";
                                if (TryFindResource("AccentRed") is SolidColorBrush brush)
                                {
                                    TxtGameStatus.Foreground = brush;
                                }
                            }
                        }

                        if (allCoreOk)
                        {
                            _notifyIcon?.ShowBalloonTip(3000, "NitroLite", 
                                $"{profile.DisplayName} algılandı. Profil: {profile.TargetMode} / {boostName}{mouseInfo}", 
                                System.Windows.Forms.ToolTipIcon.Info);
                        }
                        else
                        {
                            ShowToast($"'{profile.DisplayName}' profilinin bazı donanım ayarları uygulanamadı.", false);
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"Profil uygulanamadı: {ex.Message}", false);
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
                                    failedSteps.Add("Çalışma Modu");
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
                                        ShowToast("Varsayılan fare profiline geri dönülemedi.", false);
                                    }
                                }
                            }

                            if (endGen != _gameSessionGeneration) return;

                            bool allSucceeded = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore;
                            _hasPendingDesktopRestore = !allSucceeded;
                            if (allSucceeded)
                            {
                                _hasGameBaselineCaptured = false; // Allow next game start to capture a fresh baseline
                                ClearGameSessionRecoveryState();
                            }

                            if (allSucceeded)
                            {
                                if (TxtGameStatus != null)
                                {
                                    TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                                        ? "Durum: Beklemede (Aktif oyun yok)" 
                                        : "Durum: Devre Dışı (Manuel Kontrol)";
                                    if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                                    {
                                        TxtGameStatus.Foreground = brush;
                                    }
                                }

                                string restoredSummary = isBatteryEco 
                                    ? $"Pilde Eco profili (Quiet / Boost Devre Dışı{restoredMouseInfo})" 
                                    : $"Varsayılan profil ({_savedModeBeforeGame}{restoredMouseInfo})";

                                _notifyIcon?.ShowBalloonTip(2500, "NitroLite", 
                                    $"{profile.DisplayName} sonlandırıldı. {restoredSummary} geri yüklendi.", 
                                    System.Windows.Forms.ToolTipIcon.Info);
                                ShowToast(isBatteryEco ? "Pilde Eco profili uygulandı (Masaüstü ayarları adaptör için saklandı)." : "Masaüstü ayarları başarıyla geri yüklendi.");
                            }
                            else
                            {
                                if (TxtGameStatus != null)
                                {
                                    TxtGameStatus.Text = "Durum: Masaüstü Geri Yükleme Bekliyor";
                                    if (TryFindResource("AccentCyan") is SolidColorBrush brush)
                                    {
                                        TxtGameStatus.Foreground = brush;
                                    }
                                }

                                ShowToast($"Geri yüklenemeyen ayarlar: {string.Join(", ", failedSteps)}", false);
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
                                    ShowToast("Varsayılan fare profiline geri dönülemedi.", false);
                                }
                            }
                        }
                        else
                        {
                            if (TxtGameStatus != null)
                            {
                                TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                                    ? "Durum: Beklemede (Aktif oyun yok)" 
                                    : "Durum: Devre Dışı (Manuel Kontrol)";
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
                        ShowToast($"Profil geri yüklenemedi: {ex.Message}", false);
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
                        failed.Add("Çalışma Modu");
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
                        failed.Add("Adaptör Güç Ayarları");
                    }
                }

                if (_isGameSessionActive || retryGen != _gameSessionGeneration) return false;

                bool allSucceeded = !_pendingModeRestore && !_pendingCpuBoostRestore && !_pendingFanRestore && !_pendingMouseRestore && (_powerAutomationService?.HasPendingAcRestore != true);
                _hasPendingDesktopRestore = !allSucceeded;

                if (allSucceeded)
                {
                    _hasGameBaselineCaptured = false;
                    ClearGameSessionRecoveryState();
                    if (TxtGameStatus != null)
                    {
                        TxtGameStatus.Text = _gameService.AutoGameModeEnabled 
                            ? "Durum: Beklemede (Aktif oyun yok)" 
                            : "Durum: Devre Dışı (Manuel Kontrol)";
                        if (TryFindResource("TextSecondary") is SolidColorBrush brush)
                        {
                            TxtGameStatus.Foreground = brush;
                        }
                    }
                    ShowToast("Bekleyen masaüstü ayarları başarıyla geri yüklendi.");
                }
                else
                {
                    if (TxtGameStatus != null)
                    {
                        TxtGameStatus.Text = "Durum: Masaüstü Geri Yükleme Bekliyor";
                        if (TryFindResource("AccentCyan") is SolidColorBrush brush)
                        {
                            TxtGameStatus.Foreground = brush;
                        }
                    }
                    ShowToast($"Yeniden deneme tamamlanamadı: {string.Join(", ", failed)}", false);
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
            ? "Durum: Beklemede (Aktif oyun yok)" 
            : "Durum: Devre Dışı (Manuel Kontrol)";
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
            if (!await ApplyLightingToDeviceAsync($"'{selectedName}' profili uygulanamadı")) return;

            _appSettings.SelectedRgbPreset = selectedName;
            _appSettings.IsBacklightOff = isOff;
            _appSettings.KeyboardLighting = _lighting;
            PersistLightingState();
        }
        catch (Exception ex)
        {
            ShowToast($"Aydınlatma profili uygulanamadı: {ex.Message}", false);
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
            ShowToast($"Aydınlatma profili kaydedilemedi: {ex.Message}", false);
            return;
        }

        _appSettings.SelectedRgbPreset = name;
        _appSettings.KeyboardLighting = _lighting;
        _appSettings.IsBacklightOff = _lighting.Brightness == 0 || _lighting.Effect.Equals("OFF", StringComparison.OrdinalIgnoreCase);
        SaveSettings();
        SaveActiveRgbState();

        PopulateRgbPresets(name);
        TxtCustomPresetName.Clear();

        ShowToast($"'{name}' aydınlatma profili kaydedildi.");
    }

    private void BtnDeleteCustomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (CmbRgbPresets.SelectedItem is not string selectedName) return;

        var ok = _rgbProfileService.DeletePreset(selectedName);
        if (ok)
        {
            if (_appSettings.SelectedRgbPreset == selectedName)
            {
                _appSettings.SelectedRgbPreset = null;
                SaveSettings();
            }
            PopulateRgbPresets();
            ShowToast($"'{selectedName}' profili silindi.");
        }
        else
        {
            ShowToast("Varsayılan fabrika profilleri silinemez.", false);
        }
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
            bool measured = details.DesignCapacityMWh > 0 && details.FullChargeCapacityMWh > 0;
            bool wearEstimated = !measured || details.IsDesignCapacityEstimated || details.IsFullChargeCapacityEstimated;

            if (TxtBatHealthPercent != null)
                TxtBatHealthPercent.Text = TelemetryFormatter.MarkEstimated(measured ? $"{details.HealthPercent:F1} %" : "92.3 %", wearEstimated);

            if (TxtBatWearPercent != null)
                TxtBatWearPercent.Text = TelemetryFormatter.MarkEstimated(measured ? $"{details.WearLevelPercent:F1} %" : "7.7 %", wearEstimated);

            if (TxtBatFullChargeCap != null)
                TxtBatFullChargeCap.Text = details.FullChargeCapacityMWh > 0
                    ? TelemetryFormatter.MarkEstimated($"{details.FullChargeCapacityMWh:N0} mWh", details.IsFullChargeCapacityEstimated)
                    : TelemetryFormatter.MarkEstimated("83.637 mWh");

            if (TxtBatDesignCap != null)
                TxtBatDesignCap.Text = details.DesignCapacityMWh > 0
                    ? "Fabrika: " + TelemetryFormatter.MarkEstimated($"{details.DesignCapacityMWh:N0} mWh", details.IsDesignCapacityEstimated)
                    : "Fabrika: " + TelemetryFormatter.MarkEstimated("90.614 mWh");

            if (TxtBatVoltage != null)
                TxtBatVoltage.Text = details.VoltageVolts > 0
                    ? TelemetryFormatter.MarkEstimated($"{details.VoltageVolts:F2} V", details.IsVoltageEstimated)
                    : TelemetryFormatter.MarkEstimated("16.45 V");
        });
    }

    private async void BtnRefreshBatHealth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshBatteryHealthAsync();
            RefreshThermalHealthUi();
            await RefreshStorageHealthUi();
            ShowToast("Donanım sağlık telemetrisi güncellendi.");
        }
        catch (Exception ex)
        {
            ShowToast($"Telemetri güncellenemedi: {ex.Message}", false);
        }
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
                if (TxtDrive0Free != null) TxtDrive0Free.Text = $"{d0.FreeCapacityGB:F1} GB Boş Alan";
                if (BarDrive0 != null)
                {
                    BarDrive0.Value = d0.UsedPercent;
                    BarDrive0.Foreground = (SolidColorBrush)FindResource(d0.UsedPercent > 85 ? "AccentRed" : (d0.UsedPercent > 70 ? "AccentCyan" : "AccentGreen"));
                }
                if (TxtDrive0HealthBadge != null)
                {
                    TxtDrive0HealthBadge.Text = d0.EstHealthPercent >= 0 ? $"Sağlık: %{d0.EstHealthPercent}" : "Aşınma verisi ölçülemiyor";
                }
                if (TxtDrive0Temp != null)
                {
                    TxtDrive0Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d0.Temperature), d0.IsTemperatureEstimated);
                }
                if (TxtSubSsd1Temp != null)
                {
                    TxtSubSsd1Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d0.Temperature), d0.IsTemperatureEstimated);
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
                if (TxtDrive1Free != null) TxtDrive1Free.Text = $"{d1.FreeCapacityGB:F1} GB Boş Alan";
                if (BarDrive1 != null)
                {
                    BarDrive1.Value = d1.UsedPercent;
                    BarDrive1.Foreground = (SolidColorBrush)FindResource(d1.UsedPercent > 85 ? "AccentRed" : (d1.UsedPercent > 70 ? "AccentCyan" : "AccentGreen"));
                }
                if (TxtDrive1HealthBadge != null)
                {
                    TxtDrive1HealthBadge.Text = d1.EstHealthPercent >= 0 ? $"Sağlık: %{d1.EstHealthPercent}" : "Aşınma verisi ölçülemiyor";
                }
                if (TxtDrive1Temp != null)
                {
                    TxtDrive1Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d1.Temperature), d1.IsTemperatureEstimated);
                }
                if (TxtSubSsd2Temp != null)
                {
                    TxtSubSsd2Temp.Text = TelemetryFormatter.MarkEstimated(TelemetryFormatter.FormatTemperature(d1.Temperature), d1.IsTemperatureEstimated);
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
                    TxtStorageBadge.Text = "Sürücü Uyarısı Mevcut";
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("AccentRed");
                }
                else if (allHealthy)
                {
                    TxtStorageBadge.Text = "Tüm Sürücüler Sağlıklı";
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                }
                else
                {
                    TxtStorageBadge.Text = "Sürücü Sağlığı Bilinmiyor";
                    TxtStorageBadge.Foreground = (SolidColorBrush)FindResource("TextSecondary");
                }
            }
        });
    }

    private void RefreshThermalHealthUi()
    {
        var report = _thermalService.GetReport();
        var trend = _thermalService.GetStabilityTrend(_selectedTrendDays);
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;

            // 14 / 30 Day Thermal Stability Trend UI
            if (TxtStabilityScore != null)
            {
                TxtStabilityScore.Text = "~" + TelemetryFormatter.FormatStabilityScore(trend.StabilityScorePercent);
                TxtStabilityScore.Foreground = (SolidColorBrush)FindResource(
                    trend.StatusLevel == ThermalStatusLevel.Excellent ? "AccentGreen" :
                    (trend.StatusLevel == ThermalStatusLevel.Good ? "AccentCyan" : "AccentRed"));
            }

            // Thermal resistance uses the estimated CPU power (no package power sensor), so every value
            // derived from it in this card is shown as an estimate.
            if (TxtStabilityStatus != null)
            {
                TxtStabilityStatus.Text = "Tahmini: " + trend.StatusText;
            }

            if (TxtStabilitySlope != null)
            {
                string slopeState = trend.DegradationSlopePerDay <= 0.005 ? " (Stabil)" : " (Artış Eğiliminde)";
                TxtStabilitySlope.Text = $"Eğim: {TelemetryFormatter.FormatThermalSlope(trend.DegradationSlopePerDay)}{slopeState}";
            }

            if (TxtStabilityBadge != null)
            {
                TxtStabilityBadge.Text = trend.StatusLevel == ThermalStatusLevel.Excellent ? "Nominal (tahmini)" :
                    (trend.StatusLevel == ThermalStatusLevel.Good ? "Kararlı (tahmini)" : "Bakım Önerilebilir (tahmini)");
                TxtStabilityBadge.Foreground = (SolidColorBrush)FindResource(
                    trend.StatusLevel == ThermalStatusLevel.Excellent ? "AccentGreen" :
                    (trend.StatusLevel == ThermalStatusLevel.Good ? "AccentCyan" : "AccentRed"));
            }

            if (BarStabilityScore != null)
            {
                BarStabilityScore.Value = trend.StabilityScorePercent;
                BarStabilityScore.Foreground = (SolidColorBrush)FindResource(
                    trend.StatusLevel == ThermalStatusLevel.Excellent ? "AccentGreen" :
                    (trend.StatusLevel == ThermalStatusLevel.Good ? "AccentCyan" : "AccentRed"));
            }

            if (TxtPasteHealthPercent != null)
            {
                TxtPasteHealthPercent.Text = $"~{report.PasteHealthPercent:F1} %";
                TxtPasteHealthPercent.Foreground = (SolidColorBrush)FindResource(report.PasteHealthPercent >= 90 ? "AccentGreen" : (report.PasteHealthPercent >= 80 ? "AccentCyan" : "AccentRed"));
            }

            if (TxtThermalResistance != null)
            {
                string tag = report.ThermalResistanceRatio <= 2.20 ? " (Optimal)" : (report.ThermalResistanceRatio <= 2.55 ? " (Normal)" : " (Yüksek)");
                TxtThermalResistance.Text = $"Direnç: ~{report.ThermalResistanceRatio:F2} °C/W{tag}{TelemetryFormatter.EstimatedSuffix}";
            }

            if (TxtActiveFanHours != null)
                TxtActiveFanHours.Text = $"{report.ActiveFanHours:F1} Saat";

            if (TxtAvgFanRpm != null)
                TxtAvgFanRpm.Text = $"Ortalama: ~{report.AvgFanRpm} RPM";

            if (TxtPassiveFanHours != null)
                TxtPassiveFanHours.Text = $"{report.PassiveFanHours:F1} Saat";

            if (TxtFanBearingHealth != null)
                TxtFanBearingHealth.Text = $"~{report.FanBearingHealthPercent:F1} %";

            if (TxtThermalBadge != null)
            {
                TxtThermalBadge.Text = "Tahmini: " + report.PasteHealthStatus;
                TxtThermalBadge.Foreground = (SolidColorBrush)FindResource(report.PasteHealthPercent >= 90 ? "AccentGreen" : (report.PasteHealthPercent >= 80 ? "AccentCyan" : "AccentRed"));
            }

            if (TxtThermalRecommendation != null)
                TxtThermalRecommendation.Text = report.Recommendation +
                    " Not: Bu değerlendirme tahminidir; CPU gücü sensörden değil kullanım ve frekanstan hesaplanır. Termal macun bakımı kararı için tek başına yeterli değildir.";

            if (TxtSubMoboTemp != null && (string.IsNullOrEmpty(TxtSubMoboTemp.Text) || TxtSubMoboTemp.Text == "-- °C"))
            {
                TxtSubMoboTemp.Text = TelemetryFormatter.MarkEstimated("48 °C");
            }
        });
    }

    private void RadioTrendPeriod_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag != null && int.TryParse(rb.Tag.ToString(), out int days))
        {
            _selectedTrendDays = days;
            if (TxtStabilityDaysSubtitle != null)
            {
                TxtStabilityDaysSubtitle.Text = $" (Son {days} Günlük Veri Seti)";
            }
            RefreshThermalHealthUi();
        }
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

            TxtBatteryHeader.Text = $"{status.BatteryPercent}%";
            TxtBatLevelBig.Text = $"{status.BatteryPercent}%";
            BarBatBig.Value = status.BatteryPercent;

            TxtBatStateDesc.Text = status.IsPluggedIn
                ? (status.IsCharging ? "Adaptöre Bağlı (Şarj Ediliyor)" : "Adaptöre Bağlı (Sınırda)")
                : "Pilde Çalışıyor (Deşarj)";

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
                TxtBatRemainingTime.Text = $"Kalan Süre: {TelemetryFormatter.FormatRemainingTime(status.EstimatedRemainingMinutes, status.IsPluggedIn, status.IsCharging)}";
            }

            if (status.IsOvercharged)
            {
                BannerOvercharge.Visibility = Visibility.Visible;
                TxtOverchargeMsg.Text = $"Pil seviyesi %{status.BatteryPercent} seviyesine ulaştı. Adaptörü çıkararak deşarj işlemini başlatabilirsiniz.";
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
            _notifyIcon?.ShowBalloonTip(3000, "NitroLite", $"Pil %{percent} seviyesine ulaştı (%80 eşiği aşıldı).", System.Windows.Forms.ToolTipIcon.Warning);
        });
    }

    private void OnDischargeTargetReached()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            BtnToggleDischarge.Content = "Deşarj İşlemini Başlat";
            BtnToggleDischarge.Background = (SolidColorBrush)FindResource("AccentCyan");
            TxtDischargeStatus.Text = "Hedef seviyeye (%80) ulaşıldı. Şarj adaptörünü bağlayabilirsiniz.";
            TxtDischargeStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");

            _notifyIcon?.ShowBalloonTip(4000, "NitroLite", "Pil seviyesi %80 sınırına ulaştı. Adaptörü bağlayabilirsiniz.", System.Windows.Forms.ToolTipIcon.Info);
            ShowToast("Pil seviyesi %80 hedef sınırına indirildi.");
        });
    }

    private void UpdateModeUi(OperatingMode mode)
    {
        _currentMode = mode;
        if (TxtModeHeader != null)
        {
            TxtModeHeader.Text = mode switch
            {
                OperatingMode.Quiet => "SESSİZ",
                OperatingMode.Default => "DENGELİ",
                OperatingMode.Extreme => "PERFORMANS",
                OperatingMode.Turbo => "TURBO",
                _ => mode.ToString().ToUpperInvariant()
            };
        }

        // Reset all mode badges and card buttons
        ResetModeCard(BtnModeQuiet, IndQuiet, TxtIndQuiet);
        ResetModeCard(BtnModeDefault, IndDefault, TxtIndDefault);
        ResetModeCard(BtnModeExtreme, IndExtreme, TxtIndExtreme);
        ResetModeCard(BtnModeTurbo, IndTurbo, TxtIndTurbo);

        switch (mode)
        {
            case OperatingMode.Quiet:
                SetModeCardActive(BtnModeQuiet, IndQuiet, TxtIndQuiet, TryFindResource("AccentCyan") as SolidColorBrush ?? System.Windows.Media.Brushes.Cyan);
                break;
            case OperatingMode.Default:
                SetModeCardActive(BtnModeDefault, IndDefault, TxtIndDefault, TryFindResource("AccentGreen") as SolidColorBrush ?? System.Windows.Media.Brushes.Green);
                break;
            case OperatingMode.Extreme:
                SetModeCardActive(BtnModeExtreme, IndExtreme, TxtIndExtreme, TryFindResource("AccentRed") as SolidColorBrush ?? System.Windows.Media.Brushes.Red);
                break;
            case OperatingMode.Turbo:
                SetModeCardActive(BtnModeTurbo, IndTurbo, TxtIndTurbo, TryFindResource("AccentRed") as SolidColorBrush ?? System.Windows.Media.Brushes.Red);
                break;
        }
    }

    private void ShowUnknownModeUi()
    {
        if (TxtModeHeader != null) TxtModeHeader.Text = "BİLİNMİYOR";
        ResetModeCard(BtnModeQuiet, IndQuiet, TxtIndQuiet);
        ResetModeCard(BtnModeDefault, IndDefault, TxtIndDefault);
        ResetModeCard(BtnModeExtreme, IndExtreme, TxtIndExtreme);
        ResetModeCard(BtnModeTurbo, IndTurbo, TxtIndTurbo);
    }

    private void ResetModeCard(WpfButton? btn, Border? ind, TextBlock? txt)
    {
        if (ind != null)
        {
            ind.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x1E, 0x23, 0x2F));
            ind.BorderBrush = (SolidColorBrush)FindResource("BorderDark");
        }
        if (txt != null)
        {
            txt.Text = "SEÇ";
            txt.Foreground = (SolidColorBrush)FindResource("TextMuted");
        }
        if (btn != null)
        {
            btn.BorderBrush = (SolidColorBrush)FindResource("BorderDark");
        }
    }

    private void SetModeCardActive(WpfButton? btn, Border? ind, TextBlock? txt, SolidColorBrush accent)
    {
        if (ind != null)
        {
            ind.Background = accent;
            ind.BorderBrush = accent;
        }
        if (txt != null)
        {
            txt.Text = "AKTİF";
            txt.Foreground = System.Windows.Media.Brushes.White;
        }
        if (btn != null)
        {
            btn.BorderBrush = accent;
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
            ShowToast($"{failurePrefix}: klavye komutu reddedildi veya Acer servisine ulaşılamadı. Ayar kaydedilmedi.", false);
        }
        return ok;
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
            if (await ApplyLightingToDeviceAsync("Aydınlatma ayarı uygulanamadı"))
            {
                PersistLightingState();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Aydınlatma ayarı uygulanamadı: {ex.Message}", false);
        }
    }

    private void UpdateLightingUi(LightingConfig cfg)
    {
        SetLightingSlidersSilently(() =>
        {
            SliderBrightness.Value = cfg.Brightness;
            SliderSpeed.Value = cfg.Speed;
        });
        TxtBrightnessVal.Text = cfg.Brightness == 0 ? "0 / 9 (Kapalı)" : $"{cfg.Brightness} / 9";
        TxtSpeedVal.Text = $"{cfg.Speed} / 9";

        // Zones
        if (cfg.LEDs.Count >= 4)
        {
            PreviewZone1.Background = new BrushConverter().ConvertFromString(cfg.LEDs[0]) as WpfBrush;
            PreviewZone2.Background = new BrushConverter().ConvertFromString(cfg.LEDs[1]) as WpfBrush;
            PreviewZone3.Background = new BrushConverter().ConvertFromString(cfg.LEDs[2]) as WpfBrush;
            PreviewZone4.Background = new BrushConverter().ConvertFromString(cfg.LEDs[3]) as WpfBrush;
        }
    }

    // ==========================================
    // UI Event Handlers
    // ==========================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
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
        Show();
        WindowState = WindowState.Normal;
        UpdateTelemetryPollingRate();
        _ = ReassertKeyboardLightingAsync(isBoot: false);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
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
                    ShowToast("Ayarlar geri yüklenemedi; yeniden deneyebilmeniz için uygulama açık bırakıldı.", false);
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
        catch (Exception ex) { ShowToast($"Çıkış tamamlanamadı: {ex.Message}", false); }
        finally { _exitRequested = false; }
    }
    private void FinishExit()
    {
        _isExiting = true;
        SaveWindowPosition();

        _batteryService.StatusUpdated -= OnBatteryStatusUpdated;
        _batteryService.OverchargeAlert -= OnOverchargeAlert;
        _batteryService.DischargeTargetReached -= OnDischargeTargetReached;
        _gameService.GameStarted -= OnGameStarted;
        _gameService.GameEnded -= OnGameEnded;

        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        _agentClient.ServiceEventReceived -= OnAgentServiceEvent;

        _pollTimer?.Stop();
        _toastTimer?.Stop();

        _thermalService.SaveData();
        _hardwareService.Dispose();
        if (_osdWindow != null)
        {
            _osdWindow.IsExiting = true;
            _osdWindow.Close();
        }
        _powerAutomationService?.Dispose();
        _fpsMonitorService.Dispose();
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
            _thermalService.SaveData();
            _hardwareService.Dispose();
            _powerAutomationService?.Dispose();
            _fpsMonitorService.Dispose();
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

        if (NavBattery.IsChecked == true)
        {
            await RefreshBatteryHealthAsync();
            RefreshThermalHealthUi();
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
                catch (Exception ex) { ShowToast($"Mod tuşu işlenemedi: {ex.Message}", false); }
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

    private async Task HandleModeSwitchKeyAsync()
    {
        // Ignore key repeats while the previous switch is still being applied.
        if (Interlocked.Exchange(ref _modeSwitchKeyBusy, 1) != 0) return;
        try
        {
            var current = await _agentClient.TryGetOperatingModeAsync() ?? _currentMode;
            bool onAc = _powerAutomationService?.LastAcStatus
                        ?? (System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Offline);
            var next = ModeSwitchKey.Next(current, onAc);
            await SetManualModeAsync(next);
        }
        finally
        {
            Interlocked.Exchange(ref _modeSwitchKeyBusy, 0);
        }
    }

    internal async Task<bool> SetManualModeAsync(OperatingMode mode)
    {
        await _gameProfileTransitionLock.WaitAsync();
        try
        {
            bool ok = await SwitchModeAsync(mode);
            if (ok)
            {
                _currentMode = mode;
                UpdateDesktopBaselineForManualChange(mode: mode);
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
            ShowToast($"Çalışma modu uygulanamadı: {ex.Message}", false);
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
                ShowToast($"Çalışma modu: {mode}");
                return true;
            }
            else
            {
                ShowToast($"Çalışma modu değiştirilemedi: {mode}", false);
                return false;
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Çalışma modu hatası: {ex.Message}", false);
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
                ShowToast(mode == FanMode.Auto ? "Otomatik fan eğrisi etkinleştirildi." : "Maksimum fan devri etkinleştirildi.");
            }
            else
            {
                ShowToast("Fan modu uygulanamadı.", false);
            }
            return ok;
        }
        catch (Exception ex)
        {
            ShowToast($"Fan modu uygulanamadı: {ex.Message}", false);
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
                CancelActiveLightingReassertion();
                if (hex == "#000000")
                {
                    await TurnOffLightsAsync();
                    return;
                }

                if (_lighting.Brightness == 0)
                {
                    _lighting.Brightness = 5;
                }

                _lighting.Color = hex;
                _lighting.Effect = "STATIC";
                _lighting.LEDs = new List<string> { hex, hex, hex, hex };

                UpdateLightingUi(_lighting);
                SuppressEffectComboEvents(() => UpdateEffectComboBox("STATIC"));
                if (!await ApplyLightingToDeviceAsync("Aydınlatma rengi ayarlanamadı")) return;
                _appSettings.IsBacklightOff = false;
                PersistLightingState();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Aydınlatma rengi ayarlanamadı: {ex.Message}", false);
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
            ShowToast($"Aydınlatma kapatılamadı: {ex.Message}", false);
        }
    }

    private async Task TurnOffLightsAsync()
    {
        CancelActiveLightingReassertion();
        _lighting.Brightness = 0;
        _lighting.Effect = "OFF";
        _lighting.Color = "#000000";
        _lighting.LEDs = new List<string> { "#000000", "#000000", "#000000", "#000000" };

        SetLightingSlidersSilently(() => SliderBrightness.Value = 0);
        TxtBrightnessVal.Text = "0 / 9 (Kapalı)";

        PreviewZone1.Background = System.Windows.Media.Brushes.Black;
        PreviewZone2.Background = System.Windows.Media.Brushes.Black;
        PreviewZone3.Background = System.Windows.Media.Brushes.Black;
        PreviewZone4.Background = System.Windows.Media.Brushes.Black;

        SuppressEffectComboEvents(() => UpdateEffectComboBox("OFF"));

        if (!await ApplyLightingToDeviceAsync("Aydınlatma kapatılamadı")) return;

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

                _lighting.Effect = effectName;

                if (_lighting.Brightness == 0)
                {
                    _lighting.Brightness = 5;
                }
                UpdateLightingUi(_lighting);

                if (!await ApplyLightingToDeviceAsync("Aydınlatma efekti ayarlanamadı")) return;
                _appSettings.IsBacklightOff = false;
                PersistLightingState();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Aydınlatma efekti ayarlanamadı: {ex.Message}", false);
        }
    }

    // Sliders only update the preview and in-memory state immediately; the device command and the
    // settings write are debounced (ScheduleLightingApply) so a drag sends one command, not one per tick.
    private void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingLightingUi || TxtBrightnessVal == null) return;
        CancelActiveLightingReassertion();
        var val = (int)e.NewValue;
        _lighting.Brightness = val;
        _appSettings.IsBacklightOff = IsLightingOff(_lighting);
        _appSettings.KeyboardLighting = _lighting;

        if (val == 0)
        {
            TxtBrightnessVal.Text = "0 / 9 (Kapalı)";
            PreviewZone1.Background = System.Windows.Media.Brushes.Black;
            PreviewZone2.Background = System.Windows.Media.Brushes.Black;
            PreviewZone3.Background = System.Windows.Media.Brushes.Black;
            PreviewZone4.Background = System.Windows.Media.Brushes.Black;
        }
        else
        {
            UpdateLightingUi(_lighting);
        }

        ScheduleLightingApply();
    }

    private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _isInitializingUi || _isApplyingPreset || _isSyncingLightingUi || TxtSpeedVal == null) return;
        CancelActiveLightingReassertion();
        var val = (int)e.NewValue;
        TxtSpeedVal.Text = $"{val} / 9";
        _lighting.Speed = val;
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
            ShowToast("30 sn aydınlatma zaman aşımı değiştirilemedi: klavye komutu reddedildi veya Acer servisine ulaşılamadı.", false);
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
                ShowToast(enable ? "%80 şarj sınırı etkinleştirildi (Acer Care Center)." : "Şarj sınırı kapatıldı; pil %100'e kadar şarj olacak.");
            }
            else
            {
                SetWatchdogCheckboxSilently(!enable);
                ShowToast($"Şarj sınırı değiştirilemedi: {_careCenterClient.LastError ?? "bilinmeyen hata"}", false);
            }
        }
        catch (Exception ex)
        {
            SetWatchdogCheckboxSilently(!enable);
            ShowToast($"Şarj sınırı değiştirilemedi: {ex.Message}", false);
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
            _batteryService.StopSafeDischarge();
            BtnToggleDischarge.Content = "Deşarj İşlemini Başlat";
            BtnToggleDischarge.Background = (SolidColorBrush)FindResource("AccentCyan");
            TxtDischargeStatus.Text = "Durum: Durduruldu.";
            TxtDischargeStatus.Foreground = (SolidColorBrush)FindResource("TextSecondary");
        }
        else
        {
            var st = _batteryService.CurrentStatus;
            if (st.IsPluggedIn)
            {
                var res = System.Windows.MessageBox.Show(this, 
                    "Adaptör bağlıyken batarya deşarj edilemez.\n\nLütfen adaptör bağlantısını kesin ve işlemi başlatmak için 'Evet' seçeneğine tıklayın.", 
                    "Adaptör Bağlantısı", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (res != MessageBoxResult.Yes) return;
            }

            if (st.BatteryPercent <= 80)
            {
                ShowToast($"Pil seviyesi (%{st.BatteryPercent}) zaten hedef sınırın (%80) altındadır.", isSuccess: false);
                return;
            }

            _batteryService.StartSafeDischarge();
            BtnToggleDischarge.Content = "Deşarjı Durdur";
            BtnToggleDischarge.Background = (SolidColorBrush)FindResource("AccentRed");
            TxtDischargeStatus.Text = "Deşarj işlemi aktif: Pil seviyesi %80 sınırına indiriliyor...";
            TxtDischargeStatus.Foreground = (SolidColorBrush)FindResource("AccentCyan");
        }
    }

    private void RefreshServiceList()
    {
        ListServiceStatuses.ItemsSource = _debloatService.GetServiceStatuses();
        bool isNsRunning = _debloatService.IsNitroSenseRunning();
        if (TxtNitroSenseRunningBadge != null)
        {
            TxtNitroSenseRunningBadge.Text = isNsRunning ? " (Arka Planda Aktif)" : " (Devre Dışı)";
            TxtNitroSenseRunningBadge.Foreground = isNsRunning 
                ? (SolidColorBrush)FindResource("AccentRed") 
                : (SolidColorBrush)FindResource("AccentGreen");
        }
    }

    private void BtnKillNitroSense_Click(object sender, RoutedEventArgs e)
    {
        _debloatService.DisableNitroSenseBackgroundExecution();
        int killed = _debloatService.KillNitroSenseProcesses();
        RefreshServiceList();
        ShowToast($"NitroSense sonlandırıldı ({killed} işlem).");
    }

    private async void BtnApplyDebloat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirm = System.Windows.MessageBox.Show(this, 
                "Altı adet OEM telemetri servisi devre dışı bırakılacak ve yaklaşık 214 MB bellek tasarrufu sağlanacaktır.\n\nDonanım kontrolleri etkilenmeyecektir. Devam edilsin mi?",
                "Servis Yapılandırması", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                var ok = await _debloatService.ApplyTier1DebloatAsync();
                RefreshServiceList();
                if (ok)
                {
                    ShowToast("Telemetri servisleri devre dışı bırakıldı (~214 MB).");
                }
                else
                {
                    var msg = !string.IsNullOrEmpty(_debloatService.LastError) 
                        ? $"Bazı servisler devre dışı bırakılamadı: {_debloatService.LastError}" 
                        : "Servis devre dışı bırakma işlemi başarısız oldu (Yönetici yetkisi reddedilmiş olabilir).";
                    ShowToast(msg, false);
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler yapılandırılamadı: {ex.Message}", false);
        }
    }

    private async void BtnRestoreServices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _debloatService.RestoreTier1ServicesAsync();
            RefreshServiceList();
            if (ok)
            {
                ShowToast("Önceki servis yapılandırması geri yüklendi.");
            }
            else
            {
                var msg = !string.IsNullOrEmpty(_debloatService.LastError) 
                    ? $"Bazı servisler geri yüklenemedi: {_debloatService.LastError}" 
                    : "Servisleri geri yükleme işlemi başarısız oldu (Yönetici yetkisi reddedilmiş olabilir).";
                ShowToast(msg, false);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Servisler geri yüklenemedi: {ex.Message}", false);
        }
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAutoStart == null) return;
        _debloatService.SetNitroLiteAutoStart(ChkAutoStart.IsChecked == true, ChkStartMinimized?.IsChecked == true);
        ShowToast(ChkAutoStart.IsChecked == true ? "Başlangıçta otomatik çalıştırma etkin" : "Başlangıçta çalıştırma devre dışı");
    }

    private void ChkStartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkStartMinimized == null) return;
        _appSettings.StartMinimized = ChkStartMinimized.IsChecked == true;
        SaveSettings();
        _debloatService.SetNitroLiteAutoStart(ChkAutoStart.IsChecked == true, _appSettings.StartMinimized);
        ShowToast(_appSettings.StartMinimized ? "Sistem tepsisinde başlatma etkin" : "Normal başlangıç etkin");
    }

    private async void BtnCleanRamCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var res = await _memoryService.CleanMemoryAsync();
            if (res.FreedMegabytes > 50)
            {
                string freedStr = res.FreedMegabytes >= 1024 
                    ? $"{res.FreedMegabytes / 1024.0:F1} GB" 
                    : $"{res.FreedMegabytes:F0} MB";
                ShowToast($"{freedStr} bellek temizlendi ({res.ProcessesOptimized} işlem).");
                if (TxtRamCleanStatus != null)
                {
                    TxtRamCleanStatus.Text = $" ({freedStr} Boşaltıldı)";
                    TxtRamCleanStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                }
            }
            else
            {
                ShowToast("Bellek önbelleği optimize durumda.");
                if (TxtRamCleanStatus != null)
                {
                    TxtRamCleanStatus.Text = " (Optimize)";
                    TxtRamCleanStatus.Foreground = (SolidColorBrush)FindResource("AccentCyan");
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Bellek temizlenemedi: {ex.Message}", false);
        }
    }

    private void ChkAutoRamClean_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || ChkAutoRamClean == null) return;
        _appSettings.AutoRamCleanerEnabled = ChkAutoRamClean.IsChecked == true;
        SaveSettings();
        ShowToast(_appSettings.AutoRamCleanerEnabled ? "Otomatik bellek temizleme etkin" : "Otomatik bellek temizleme devre dışı");
    }

    private async void CmbDcPowerMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi || CmbDcPowerMode?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (Enum.TryParse<DcPowerProfilePreference>(tag, out var pref))
        {
            _appSettings.DcPowerMode = pref;
            SaveSettings();
            ShowToast($"Pilde güç tercihi: {pref}");
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
            ? "Otomatik ekran tazeleme hızı geçişi etkin (60Hz / 165Hz)"
            : "Otomatik ekran tazeleme hızı geçişi devre dışı");
    }
}

