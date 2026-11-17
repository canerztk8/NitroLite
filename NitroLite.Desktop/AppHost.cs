using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using NitroLite.Desktop.Themes;

#if WINDOWS
using NitroLite.Core.Hardware.Wmi;
using NitroLite.Windows;
#else
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;
#endif

namespace NitroLite.Desktop;

/// <summary>
/// Everything the platform supplies, built once and shared by the window and the one-shot commands.
/// </summary>
/// <remarks>
/// A record rather than a tuple because the list is long enough that positional access stopped being readable, and
/// because the headless commands need the same pieces the window does without a view model or a theme applier.
/// </remarks>
public sealed record PlatformServices(
    AppSettings Settings,
    string SettingsPath,
    IHardwareController? Hardware,
    IPlatformSnapshot? Platform,
    IRescannableBackend? Rescan,
    IAutoStartService? AutoStart,
    GameProfileService? GameProfiles,
    SchoolModeService? SchoolMode,
    RefreshRateAutomation? RefreshRate,
    Func<(bool OnAc, int? BatteryPercent)>? PowerState,
    ICpuBoostAccess? CpuBoost,
    IGameLibraryScanner? GameLibrary,
    IMouseProfileService? MouseProfiles,
    IHardwareMonitor? Monitor,
    IMemoryCleaner? MemoryCleaner,
    IOpenRgbStatusProvider? OpenRgb,
    IBatteryHealthService? BatteryHealth,
    IStorageHealthService? StorageHealth,
    IDebloatService? Debloat,
    ISystemToolsService? SystemTools,
    IUpdateService? Updates,
    IDiagnosticsService? Diagnostics,
    IDirectWmiControl? DirectWmi,
    IRunAtStartupService? RunAtStartup,
    INitroKeyListener? NitroKey,
    INitroSenseWatcher? NitroSense,
    SettingsLoadResult? SettingsLoad);

/// <summary>
/// The composition root: settings, language, the hardware backend and the view model, wired once at startup.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place that decides which backend the app talks to, and it decides it at compile time as well as at
/// run time. The <c>net10.0</c> build sees <c>NitroLite.Linux</c> and nothing else; the <c>net10.0-windows</c> build
/// sees <c>NitroLite.Windows</c> and nothing else. A reference that leaked across that line would fail to compile,
/// which is the point: the Linux build must never be able to drag <c>System.Management</c> or the registry into a
/// process that has to run on a Linux desktop.
/// </para>
/// <para>
/// It is also the only place that decides which platform services exist. Game detection gets the platform's process
/// scanner, school mode gets the platform's brightness and boost backends, the Game Profiles page gets the platform's
/// library scanner, and all of them get the same settings object the rest of the app uses — so a capability the probe
/// found and a capability the service writes can never disagree.
/// </para>
/// <para>
/// Nothing here writes to the hardware. The probe only reads, and the controller is created with the settings object
/// the rest of the app uses so the read-back counters survive a restart.
/// </para>
/// </remarks>
public sealed class AppHost : IDisposable
{
    private readonly IHardwareController? _hardware;
    private readonly INitroKeyListener? _nitroKey;
    private readonly INitroSenseWatcher? _nitroSense;
    private readonly IBatteryHealthService? _batteryHealth;

    private AppHost(PlatformServices services, IDialogService? dialogs)
    {
        _hardware = services.Hardware;
        _nitroKey = services.NitroKey;
        _nitroSense = services.NitroSense;
        _batteryHealth = services.BatteryHealth;

        // The battery's own loop is started here rather than by the page: the overcharge banner and the discharge
        // estimate have to be live while the page is closed, which is the whole point of a watchdog.
        try
        {
            _batteryHealth?.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Pil gözcüsü başlatılamadı", ex.Message);
        }

        ViewModel = new MainViewModel(
            services.Settings,
            services.Hardware,
            services.Platform,
            services.SettingsPath,
            services.GameProfiles,
            services.SchoolMode,
            new RgbProfileService(),
            DesktopThemeManager.Instance,
            services.Rescan,
            services.AutoStart,
            services.RefreshRate,
            services.PowerState,
            services.CpuBoost,
            services.GameLibrary,
            services.MouseProfiles,
            services.Monitor,
            services.MemoryCleaner,
            dialogs,
            services.OpenRgb,
            services.BatteryHealth,
            services.StorageHealth,
            services.Debloat,
            services.SystemTools,
            services.Updates,
            services.Diagnostics,
            services.DirectWmi,
            services.RunAtStartup,
            services.NitroKey,
            services.NitroSense);

        // A settings file that had to be discarded is the one startup failure the user has to be told about: the app
        // is running on the backup or on defaults, and the preferences they set may be gone. The message is handed
        // over rather than raised here because the window has not subscribed to Status yet.
        if (services.SettingsLoad is { PrimaryCorrupt: true } load)
        {
            ViewModel.ReportSettingsRecovered(load.Source);
        }
    }

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// The Nitro-key listener, owned here so the window can subscribe to it and the view model can ask whether the
    /// machine has the button. Null only when the platform could not build one at all.
    /// </summary>
    public INitroKeyListener? NitroKey => _nitroKey;

    /// <summary>
    /// The NitroSense watcher, owned here for the same reason: the shell subscribes to it and the view model asks
    /// whether the platform can watch at all. Null on a platform that cannot.
    /// </summary>
    public INitroSenseWatcher? NitroSense => _nitroSense;

    /// <summary>Builds everything. Never throws: a machine with no Acer support gets a window that explains it.</summary>
    public static AppHost Create(IDialogService? dialogs = null) => new(CreateServices(), dialogs);

    /// <summary>
    /// Builds the platform half only. Used by the window and by the one-shot commands, so a <c>--cli</c> run talks to
    /// exactly the backend the window would.
    /// </summary>
    public static PlatformServices CreateServices()
    {
#if WINDOWS
        // An update that was interrupted leaves the install folder with a missing file and its only good copy under
        // a .old name. The repair has to run before anything reads those files, which is why it is the first thing
        // the composition root does.
        if (OperatingSystem.IsWindows()) RepairInstallFolder();
#endif

        var settingsPath = AppPaths.GetDataFile("settings.json");
        var backupPath = settingsPath + ".bak";
        var legacyPath = AppPaths.GetDataFile("config.json");

        var freshInstall = !File.Exists(settingsPath) && !File.Exists(backupPath);
        var load = SettingsManager.LoadSettings(settingsPath, backupPath, legacyPath);
        var settings = load.Settings;

        if (load.PrimaryCorrupt)
        {
            AppLog.Warn("Ayarlar dosyası bozuktu, yedekten okundu", load.PrimaryError);
        }

        // The stored choice wins; a fresh install follows the desktop language; a file written before the language
        // field existed stays Turkish, which is what its user has been reading.
        var language = Loc.ResolveStartupLanguage(settings.Language, freshInstall);
        Loc.SetLanguage(language);
        settings.Language = language;

        IHardwareController? hardware = null;
        IPlatformSnapshot? platform = null;
        IRescannableBackend? rescan = null;
        IAutoStartService? autoStart = null;
        GameProfileService? gameProfiles = null;
        SchoolModeService? schoolMode = null;
        RefreshRateAutomation? refreshRate = null;
        Func<(bool OnAc, int? BatteryPercent)>? powerState = null;
        ICpuBoostAccess? cpuBoost = null;
        IGameLibraryScanner? gameLibrary = null;
        IMouseProfileService? mouseProfiles = null;
        IHardwareMonitor? monitor = null;
        IMemoryCleaner? memoryCleaner = null;
        IOpenRgbStatusProvider? openRgb = null;
        IBatteryHealthService? batteryHealth = null;
        IStorageHealthService? storageHealth = null;
        IDebloatService? debloat = null;
        ISystemToolsService? systemTools = null;
        IUpdateService? updates = null;
        IDiagnosticsService? diagnostics = null;
        IDirectWmiControl? directWmi = null;
        IRunAtStartupService? runAtStartup = null;
        INitroKeyListener? nitroKey = null;
        INitroSenseWatcher? nitroSense = null;

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                (hardware, platform, rescan, autoStart, gameProfiles, schoolMode, refreshRate, powerState) =
                    CreateWindows(settings);

                // The same object school mode writes through, so the segmented control and the state machine can never
                // disagree about which boost mode the scheme is holding.
                cpuBoost = new CpuPowerService();

                // The library scan reads the Steam registry and the Epic manifests; the mouse is a USB HID device.
                gameLibrary = new WindowsGameLibraryScanner();
                mouseProfiles = new AtkMouseProfileService();

                // The OS half of the monitoring page: model strings, load, package watts, VRAM and the standby-list
                // cleaner. Separate from the Acer backend on purpose, so a machine whose service is missing still
                // shows a useful page.
                monitor = new WindowsHardwareMonitor();
                memoryCleaner = new WindowsMemoryCleaner();

                // The battery page, the services page and the tools. Each one wraps the class the WPF front end used, so
                // the behaviour a user already knows is the behaviour they get.
                batteryHealth = new WindowsBatteryHealthService();
                storageHealth = new WindowsStorageHealthService();
                debloat = new WindowsDebloatService();
                systemTools = new WindowsSystemToolsService();
                updates = new WindowsUpdateService();
                diagnostics = new WindowsDiagnosticsService();
                runAtStartup = new WindowsRunAtStartupService();

                // The laptop's own Nitro key. Constructed here and started by the shell, because the listener has to
                // outlive the window: the whole point is that the key works while the app sits in the tray.
                nitroKey = new WindowsNitroKeyListener();

                // The button on this model never reaches the listener: the Acer service stack owns it and answers by
                // launching NitroSense. Watching for that window is the only signal left, and the shell decides what
                // to do with it.
                nitroSense = new NitroSenseWatcher();

                // The direct-WMI row is only meaningful when the hybrid controller is the one in use; a machine whose
                // backend is something else gets no row rather than a button that cannot work.
                if (hardware is HybridHardwareController hybrid) directWmi = new WindowsDirectWmiControl(hybrid);

                // The issue body names the machine model, and the WMI read behind it lives in this assembly. The shared
                // updater cannot reference it, so the composition root hands it over.
                UpdateService.MachineModelProvider = WindowsMachineModel.Read;
            }
            catch (Exception ex)
            {
                // A probe that throws is a machine with no Acer support, not a reason to refuse to start.
                AppLog.Error("Windows donanım arka ucu kurulamadı", ex.ToString());
            }
        }
#else
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var controller = LinuxSysfsHardwareController.Create(settings);
                var linux = controller.Capabilities;
                hardware = controller;
                platform = linux;
                rescan = controller;
                AppLog.Info("Linux sysfs arka ucu bağlandı",
                    $"{linux.Tier} · {linux.Identity.ModelCode} · udev={(linux.NeedsUdevRule ? "gerekli" : "tamam")}");

                var fs = RealSysfsFileSystem.Instance;
                gameProfiles = CreateLinuxGameProfiles(settings, controller, fs);
                schoolMode = CreateLinuxSchoolMode(settings, controller, linux, fs);

                // The Linux display service reports IsSupported false, so the automation is inert and the settings
                // card hides itself. Wiring it anyway keeps the composition root free of a platform branch that would
                // have to be remembered when a Wayland or XRandR backend is added.
                refreshRate = new RefreshRateAutomation(new LinuxDisplayRefreshRateService(), settings);
                cpuBoost = new LinuxCpuBoostAccess(fs);
                powerState = () =>
                {
                    var (onAc, percent) = LinuxPowerSupply.Read(fs);
                    return (onAc ?? true, percent);
                };

                // The Steam roots under the home directory. The mouse protocol is a vendor HID report with no Linux
                // backend yet, so the row stays hidden rather than offering four buttons that write nowhere.
                gameLibrary = new LinuxGameLibraryScanner();
                mouseProfiles = new NullMouseProfileService();

                // /proc and hwmon answer the same rows Windows answers through WMI and NVML. The standby-list cleaner
                // reports itself unsupported, so its card hides rather than offering a button that does nothing.
                monitor = new LinuxHardwareMonitor(fs);
                memoryCleaner = new LinuxMemoryCleaner();

                // The SDK is the third lighting path, and the only one that can say whether it answered. The status
                // line stays hidden until the fallback has actually been tried.
                openRgb = controller;

                // The battery reads /sys/class/power_supply/BAT*; the storage, debloat and repair cards report
                // themselves unsupported and hide, because none of them has a Linux equivalent that works without root.
                batteryHealth = new LinuxBatteryHealthService(fs);
                storageHealth = new LinuxStorageHealthService();
                debloat = new LinuxDebloatService();
                systemTools = new LinuxSystemToolsService();
                updates = new LinuxUpdateService(fs);
                diagnostics = new LinuxDiagnosticsService();
                runAtStartup = new LinuxRunAtStartupService();

                // The same button, read out of /dev/input instead of WMI. A machine without acer-wmi, or a user who is
                // not in the input group, gets IsSupported false and no settings row.
                nitroKey = new LinuxNitroKeyListener();
            }
            catch (Exception ex)
            {
                // A probe that throws is a machine with no Acer support, not a reason to refuse to start.
                AppLog.Error("Linux donanım arka ucu kurulamadı", ex.ToString());
            }
        }
#endif

        return new PlatformServices(
            settings, settingsPath, hardware, platform, rescan, autoStart, gameProfiles, schoolMode,
            refreshRate, powerState, cpuBoost, gameLibrary, mouseProfiles, monitor, memoryCleaner, openRgb,
            batteryHealth, storageHealth, debloat, systemTools, updates, diagnostics, directWmi, runAtStartup,
            nitroKey, nitroSense, load);
    }

#if WINDOWS
    /// <summary>
    /// Repairs an install folder an interrupted update left behind, then sweeps the leftovers of a completed one.
    /// </summary>
    /// <remarks>
    /// The sweep is skipped when the repair reported a failure. <see cref="UpdateService.RecoverInterruptedUpdate"/>
    /// deliberately leaves a <c>.old</c> file in place when it could not move it back, because that file is the only
    /// surviving copy; deleting it here would destroy exactly what the repair exists to preserve.
    /// </remarks>
    private static void RepairInstallFolder()
    {
        try
        {
            var recovery = UpdateService.RecoverInterruptedUpdate();
            if (!recovery.IsClean)
            {
                AppLog.Info("Güncelleme kalıntıları denetlendi",
                    $"geri={recovery.Restored.Count} silinen={recovery.Removed.Count} başarısız={recovery.Failed.Count}");
            }

            if (recovery.Failed.Count == 0) UpdateService.CleanUpPreviousVersion();
        }
        catch (Exception ex)
        {
            // A folder that cannot be repaired is not a reason to refuse to start: the app still runs from whatever
            // is there, and the log says what was found.
            AppLog.Warn("Yarım kalan güncelleme denetlenemedi", ex.Message);
        }
    }

    /// <summary>
    /// The Windows backend: the Acer service when it answers, direct WMI when the user asked for it, and the elevated
    /// worker behind that. The capability probe is lazy on purpose — it enumerates <c>root\WMI</c> and reads the
    /// registry, and the window must open before that finishes.
    /// </summary>
    private static (
        IHardwareController Hardware,
        IPlatformSnapshot Platform,
        IRescannableBackend Rescan,
        IAutoStartService AutoStart,
        GameProfileService GameProfiles,
        SchoolModeService SchoolMode,
        RefreshRateAutomation RefreshRate,
        Func<(bool OnAc, int? BatteryPercent)> PowerState) CreateWindows(AppSettings settings)
    {
        var identity = HardwareDetector.Current;
        var catalog = ProfileCatalog.Load();

        CapabilitySnapshot? snapshot = null;
        bool probed = false;

        // The probe runs at most once per session and only when something asks for it: the settings page's tier line,
        // the hardware report, or the WMI controller's first sensor read.
        CapabilitySnapshot? Probe()
        {
            if (probed) return snapshot;
            probed = true;
            try
            {
                snapshot = CapabilityProbe.TryFromCache(settings, identity, catalog)
                           ?? CapabilityProbe.Probe(identity, catalog);
                CapabilityProbe.Store(settings, snapshot);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Donanım yetenekleri okunamadı", ex.ToString());
            }

            return snapshot;
        }

        var readBack = new ReadBackPolicy(settings);
        var wmi = new WmiHardwareController(new WmiWorkerLauncher(), readBack, Probe);
        var agent = new AgentHardwareController(new AcerAgentClient(), new CareCenterClient());

        // The service is the default path; direct WMI is the fallback the settings page can switch on, and it is the
        // only one that can raise an elevation prompt.
        var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => settings.UseDirectWmiBackend);

        var platform = new WindowsPlatformSnapshot(
            identity, Probe, () => hybrid.ActiveBackendName, readBack, () => wmi);

        // Game detection over the Windows process table. The power state is supplied so a Turbo profile is clamped to
        // Balanced on battery instead of being written and silently refused by the firmware.
        var gameProfiles = new GameProfileService(new WindowsProcessScanner(), settings, hybrid)
        {
            PowerState = WindowsPowerStatus.Read
        };

        var schoolMode = CreateWindowsSchoolMode(settings, hybrid);

        AppLog.Info("Windows donanım arka ucu bağlandı",
            $"{identity.ModelCode} · wmi={(settings.UseDirectWmiBackend ? "etkin" : "yedek")}");

        // The Task Scheduler entry is the only way to start elevated without a UAC prompt, and it is what makes the
        // direct-WMI path usable at sign-in.
        var autoStart = new ElevatedAutoStartService();

        // The panel automation. The power state is the same reader the game profiles use, so a Turbo profile and a
        // 60 Hz drop can never disagree about whether the charger is plugged in.
        var refreshRate = new RefreshRateAutomation(new DisplayRefreshRateService(), settings);

        return (hybrid, platform, platform, autoStart, gameProfiles, schoolMode, refreshRate, WindowsPowerStatus.Read);
    }

    /// <summary>
    /// School mode over the Windows backends: the active power scheme for the boost, the WMI brightness class for the
    /// panel, and the display service for the refresh rate. The lighting hooks are wired here rather than inside the
    /// state machine so the machine-independent half stays free of the keyboard protocol.
    /// </summary>
    private static SchoolModeService CreateWindowsSchoolMode(AppSettings settings, IHardwareController hardware)
    {
        var service = new SchoolModeService(
            hardware,
            new CpuPowerService(),
            new DisplayRefreshRateService(),
            new ScreenBrightnessService(),
            settings,
            new WindowsPowerSchemeAccess())
        {
            GetCurrentOperatingMode = () => hardware.GetOperatingModeAsync().GetAwaiter().GetResult() ?? OperatingMode.Default,
            GetCurrentFanMode = () => hardware.GetFanModeAsync().GetAwaiter().GetResult() ?? FanMode.Auto,
            TurnLightingOffAsync = async () =>
            {
                var current = await hardware.GetLightingConfigAsync().ConfigureAwait(false);
                if (current == null || LightingPolicy.IsOff(current)) return false;
                return await hardware.TurnOffLightingAsync().ConfigureAwait(false);
            },
            RestoreLightingAsync = async () =>
            {
                var last = settings.LastActiveLighting ?? settings.KeyboardLighting;
                if (last == null) return;

                await hardware.SetLightingAsync(
                    last.Effect,
                    last.Brightness,
                    last.Speed,
                    last.Direction,
                    last.Color,
                    last.LEDs.ToArray()).ConfigureAwait(false);
            }
        };

        return service;
    }
#else
    /// <summary>
    /// Game detection over <c>/proc</c>. The power state is supplied so a Turbo profile is clamped to Balanced on
    /// battery instead of being written and silently refused by the firmware.
    /// </summary>
    private static GameProfileService CreateLinuxGameProfiles(
        AppSettings settings, IHardwareController hardware, ISysfsFileSystem fs)
    {
        var service = new GameProfileService(new LinuxProcessScanner(fs), settings, hardware)
        {
            // No adapter node means "unknown", and unknown must not downgrade a profile: assuming AC keeps the target
            // the user configured, which is the same answer the Windows build gives when the power state is missing.
            PowerState = () =>
            {
                var (onAc, percent) = LinuxPowerSupply.Read(fs);
                return (onAc ?? true, percent);
            }
        };

        return service;
    }

    /// <summary>
    /// School mode over the Linux backends. The lighting hooks are wired here rather than inside the state machine so
    /// the machine-independent half stays free of the keyboard protocol.
    /// </summary>
    private static SchoolModeService CreateLinuxSchoolMode(
        AppSettings settings, IHardwareController hardware, LinuxCapabilities linux, ISysfsFileSystem fs)
    {
        var service = new SchoolModeService(
            hardware,
            new LinuxCpuBoostAccess(fs),
            new LinuxDisplayRefreshRateService(),
            new LinuxScreenBrightnessService(fs),
            settings,
            new LinuxPowerSchemeAccess())
        {
            GetCurrentOperatingMode = () => hardware.GetOperatingModeAsync().GetAwaiter().GetResult() ?? OperatingMode.Default,
            GetCurrentFanMode = () => hardware.GetFanModeAsync().GetAwaiter().GetResult() ?? FanMode.Auto,
            IsModeSupported = mode => ModePolicy.IsModeSupported(linux.Hardware.SupportedOperatingModes, mode),
            TurnLightingOffAsync = async () =>
            {
                var current = await hardware.GetLightingConfigAsync().ConfigureAwait(false);
                if (current == null || LightingPolicy.IsOff(current)) return false;
                return await hardware.TurnOffLightingAsync().ConfigureAwait(false);
            },
            RestoreLightingAsync = async () =>
            {
                var last = settings.LastActiveLighting ?? settings.KeyboardLighting;
                if (last == null) return;

                await hardware.SetLightingAsync(
                    last.Effect,
                    last.Brightness,
                    last.Speed,
                    last.Direction,
                    last.Color,
                    last.LEDs.ToArray()).ConfigureAwait(false);
            }
        };

        return service;
    }
#endif

    public void Dispose()
    {
        ViewModel.Dispose();
        _nitroKey?.Dispose();
        _batteryHealth?.Dispose();
        _hardware?.Dispose();
    }
}
