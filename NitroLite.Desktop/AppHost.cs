using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Core.Hardware.Diagnostics;
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
    SettingsLoadResult? SettingsLoad,
    Task ProbeTask);

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
    private readonly Task _probeTask;

    /// <summary>
    /// The two <see cref="CpuPowerService"/> instances, held so they can be disposed.
    /// </summary>
    /// <remarks>
    /// They are separate objects on purpose — the view model's boost control and school mode's undo record must not
    /// share a hung-process slot — but both are <see cref="IDisposable"/> for one reason: a <c>powercfg.exe</c> that
    /// did not exit inside its timeout is killed by <c>Dispose</c>. Without a field here nothing could ever call it,
    /// because <see cref="ICpuBoostAccess"/> is not disposable, so a hung child outlived the app and every later
    /// boost write stayed blocked behind it.
    /// </remarks>
    private readonly IDisposable? _cpuBoost;
    private readonly SchoolModeService? _schoolMode;

    /// <summary>
    /// The OS monitor, held so NVML can be shut down.
    /// </summary>
    /// <remarks>
    /// <see cref="IHardwareMonitor"/> is not disposable, so the view model cannot release it. The handle is released
    /// by process teardown either way; disposing it here is what makes the driver see a clean shutdown rather than a
    /// process that vanished.
    /// </remarks>
    private readonly IDisposable? _monitor;

    private AppHost(PlatformServices services, IDialogService? dialogs)
    {
        _hardware = services.Hardware;
        _probeTask = services.ProbeTask;
        _nitroKey = services.NitroKey;
        _nitroSense = services.NitroSense;
        _batteryHealth = services.BatteryHealth;
        _cpuBoost = services.CpuBoost as IDisposable;
        _schoolMode = services.SchoolMode;
        _monitor = services.Monitor as IDisposable;

        // The battery's own loop is started here rather than by the page: the overcharge banner and the discharge
        // estimate have to be live while the page is closed, which is the whole point of a watchdog.
        try
        {
            _batteryHealth?.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("The battery watchdog could not be started", ex.Message);
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
            services.NitroSense,
            urlOpener: OpenInDefaultBrowser,
            probeTask: services.ProbeTask,
            platformSetup: CreatePlatformSetup(services.Platform));

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
    /// The one-time setup the settings page offers: on Linux the udev rule, the groups and the predator_v4 option,
    /// behind one password prompt. Windows has nothing to set up once, so it gets none.
    /// </summary>
    private static IPlatformSetup? CreatePlatformSetup(IPlatformSnapshot? platform)
    {
#if WINDOWS
        _ = platform;
        return null;
#else
        if (!OperatingSystem.IsLinux() || platform is not LinuxCapabilities linux) return null;

        try
        {
            return new LinuxPlatformSetup(linux, UdevGuide.EnsureRuleFile);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The Linux setup helper could not be created", ex.Message);
            return null;
        }
#endif
    }

    /// <summary>
    /// Opens a URL through the shell, the way <see cref="MainViewModel"/> expects its injected opener to. Lives here
    /// rather than as a <c>Process.Start</c> fallback in the view model so a test that forgets to inject an opener can
    /// never launch a real browser on the developer's machine.
    /// </summary>
    private static void OpenInDefaultBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                })?.Dispose();
            }
            else if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", url)?.Dispose();
            }
            else
            {
                AppLog.Warn("The link could not be opened", $"No browser launch path for this platform: {url}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The link could not be opened", $"{url}: {ex.Message}");
        }
    }

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
    /// Completes when the capability probe has run, successfully or not.
    /// </summary>
    /// <remarks>
    /// The probe starts on the thread pool from the composition root, so the window opens without waiting for it. The
    /// window awaits this before its first hardware read, which is what keeps a slow probe off the UI thread; a test
    /// awaits it instead of sleeping, which is what makes the hardware matrix deterministic.
    /// </remarks>
    public Task WhenReadyAsync() => _probeTask;

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
            AppLog.Warn("The settings file was corrupt; it was read from the backup", load.PrimaryError);
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

        // Completes when the capability probe has run, or immediately on a platform that has no probe. The window
        // awaits it before its first hardware read, so a slow probe delays the controls rather than the window.
        var probeTask = Task.CompletedTask;

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                (hardware, platform, rescan, autoStart, gameProfiles, schoolMode, refreshRate, powerState, probeTask) =
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
                AppLog.Error("The Windows hardware backend could not be set up", ex.ToString());
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
                AppLog.Info("Linux sysfs backend attached",
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
                diagnostics = new LinuxDiagnosticsService(() => linux.BuildHardwareReport());
                runAtStartup = new LinuxRunAtStartupService();

                // The same button, read out of /dev/input instead of WMI. A machine without acer-wmi, or a user who is
                // not in the input group, gets IsSupported false and no settings row.
                nitroKey = new LinuxNitroKeyListener();
            }
            catch (Exception ex)
            {
                // A probe that throws is a machine with no Acer support, not a reason to refuse to start.
                AppLog.Error("The Linux hardware backend could not be set up", ex.ToString());
            }
        }
#endif

        return new PlatformServices(
            settings, settingsPath, hardware, platform, rescan, autoStart, gameProfiles, schoolMode,
            refreshRate, powerState, cpuBoost, gameLibrary, mouseProfiles, monitor, memoryCleaner, openRgb,
            batteryHealth, storageHealth, debloat, systemTools, updates, diagnostics, directWmi, runAtStartup,
            nitroKey, nitroSense, load, probeTask);
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
                AppLog.Info("Update leftovers were checked",
                    $"restored={recovery.Restored.Count} removed={recovery.Removed.Count} failed={recovery.Failed.Count}");
            }

            if (recovery.Failed.Count == 0) UpdateService.CleanUpPreviousVersion();
        }
        catch (Exception ex)
        {
            // A folder that cannot be repaired is not a reason to refuse to start: the app still runs from whatever
            // is there, and the log says what was found.
            AppLog.Warn("The interrupted update could not be checked", ex.Message);
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
        Func<(bool OnAc, int? BatteryPercent)> PowerState,
        Task ProbeTask) CreateWindows(AppSettings settings)
    {
        var identity = HardwareDetector.Current;

        // The probe enumerates root\WMI and reads the registry. On a healthy machine that is tens of milliseconds, but
        // a machine with a damaged WMI repository answers in tens of seconds, and the old code ran it on whichever
        // thread first asked — which was the UI thread, on the first hardware read after the window opened.
        //
        // A Task rather than a Lazy, and that is the whole point. A Lazy runs its factory on whichever thread asks
        // first, so the guarantee "the probe never runs on the UI thread" would have rested on the pool thread winning
        // a race it is not obliged to win. A Task is started here, before the window exists, and every caller waits
        // for that one run instead of being able to start one of its own.
        var probeTask = Task.Run<CapabilitySnapshot?>(() =>
        {
            try
            {
                // Read here rather than once at startup: the published table is downloaded in the background, and a
                // rescan after it lands has to see it without a restart. Parsing forty profiles is cheap next to the
                // WMI walk this thread is already doing.
                var catalog = ProfileCatalog.Load();
                var result = CapabilityProbe.TryFromCache(settings, identity, catalog)
                             ?? CapabilityProbe.Probe(identity, catalog);
                CapabilityProbe.Store(settings, result);
                return result;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Hardware capabilities could not be read", ex.ToString());
                return null;
            }
        });

        // A caller that needs the answer blocks until the pool thread has it. The factory catches everything, so the
        // task cannot fault in practice; the catch is here because a hardware read is not the place to discover that
        // it did, and a null snapshot is the same answer a failed probe gives.
        CapabilitySnapshot? Probe()
        {
            try
            {
                return probeTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Hardware capabilities could not be read", ex.Message);
                return null;
            }
        }

        // The method list the diagnostics send. A live probe result carries it; a snapshot restored from the cache does
        // not, and the cache is the common case after the first launch. So the list is taken from the probe when it is
        // there and read from the schema once when it is not — on the diagnostics thread, after a failure, with a short
        // timeout, and remembered, so the extra read costs one WMI walk in a run that is already failing.
        string[]? discoveredMethods = null;
        string[] DiscoveredMethods()
        {
            if (discoveredMethods != null) return discoveredMethods;

            var probed = probeTask.IsCompletedSuccessfully ? probeTask.Result?.Wmi.Methods : null;
            if (probed is { Count: > 0 })
            {
                discoveredMethods = probed.ToArray();
                return discoveredMethods;
            }

            try
            {
                discoveredMethods = AcerWmiProbe.ReadWithTimeout(TimeSpan.FromSeconds(2)).Methods.ToArray();
            }
            catch (Exception)
            {
                // Silence is the right answer: the report still names the model, the board, the BIOS and the control.
                discoveredMethods = Array.Empty<string>();
            }

            return discoveredMethods;
        }

        // The hardware diagnostics. One POST, only when a read-back contradicts a write or the lighting fallback chain
        // runs out of transports, and nothing else: no setting, no button, no notification and no log line. The
        // identity is read at the moment of the failure rather than here, because the probe runs in the background and
        // a report sent before it lands should say "no methods known" instead of the empty answer captured at launch.
        var diagnostics = new HardwareDiagnosticClient(
            identity: () => new HardwareDiagnosticIdentity
            {
                Model = identity.ModelCode.Length > 0 ? identity.ModelCode : identity.Model,
                Board = identity.BoardProduct,
                BiosVersion = identity.BiosVersion,
                AvailableWmiMethods = DiscoveredMethods()
            });

        var readBack = new ReadBackPolicy(settings, diagnostics: diagnostics);
        var wmi = new WmiHardwareController(new WmiWorkerLauncher(), readBack, Probe, diagnostics);
        var agent = new AgentHardwareController(new AcerAgentClient(), new CareCenterClient());

        // The service is the default path; direct WMI is the fallback the settings page can switch on, and it is the
        // only one that can raise an elevation prompt.
        var hybrid = new HybridHardwareController(
            agent,
            wmiFactory: () => wmi,
            useDirectWmi: () => settings.UseDirectWmiBackend,
            // The vendor HID controller is the transport the 2025 boards moved their backlight to. It is probed
            // only when a WMI lighting write did not take, and it stays read-only until a documented frame exists.
            hidLighting: NitroLite.Windows.Core.Hardware.Lighting.HidLightingDevices.FindWriter,
            diagnostics: diagnostics);

        // The Task Scheduler entry starts this process already elevated. Use that token to bring the direct-WMI
        // backend up immediately when the Acer service is not answering, so the user never sees a UAC prompt:
        // AutoStartDirectWmiAsync is a no-op when the process is not elevated, the service is healthy, or the
        // user pinned the app to the service path.
        _ = Task.Run(async () =>
        {
            try
            {
                await hybrid.AutoStartDirectWmiAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn("The direct WMI backend could not be started at launch", ex.Message);
            }
        });

        var platform = new WindowsPlatformSnapshot(
            identity, Probe, () => hybrid.ActiveBackendName, readBack, () => wmi,
            backendDetail: () => hybrid.DescribeBackend());

        // Game detection over the Windows process table. The power state is supplied so a Turbo profile is clamped to
        // Balanced on battery instead of being written and silently refused by the firmware.
        var gameProfiles = new GameProfileService(new WindowsProcessScanner(), settings, hybrid)
        {
            PowerState = WindowsPowerStatus.Read
        };

        var schoolMode = CreateWindowsSchoolMode(settings, hybrid);

        AppLog.Info("The Windows hardware backend was attached",
            $"{identity.ModelCode} · direct WMI {(settings.UseDirectWmiBackend ? "enabled" : "fallback only")}");

        // The Task Scheduler entry is the only way to start elevated without a UAC prompt, and it is what makes the
        // direct-WMI path usable at sign-in.
        var autoStart = new ElevatedAutoStartService();

        // The panel automation. The power state is the same reader the game profiles use, so a Turbo profile and a
        // 60 Hz drop can never disagree about whether the charger is plugged in.
        var refreshRate = new RefreshRateAutomation(new DisplayRefreshRateService(), settings);

        return (hybrid, platform, platform, autoStart, gameProfiles, schoolMode, refreshRate, WindowsPowerStatus.Read,
            probeTask);
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
        // The view model first: it holds the fan curve loop and the write gate, and a step in flight reaches the
        // hardware through the controller disposed below.
        ViewModel.Dispose();

        // The NitroSense watcher is disposed here as well as in App.DisposeHost. That is not redundant: AppHost is
        // also constructed by tests and by any future headless host, and a 400 ms foreground poll with no owner is
        // exactly the kind of timer this audit exists to remove. Dispose is idempotent on the watcher.
        _nitroSense?.Dispose();
        _nitroKey?.Dispose();
        _batteryHealth?.Dispose();

        // The two boost backends and the OS monitor are IDisposable for one reason each — a hung powercfg.exe and an
        // NVML session — and neither interface exposes Dispose, so this is the only place that can release them.
        _schoolMode?.Dispose();
        _cpuBoost?.Dispose();
        _monitor?.Dispose();

        _hardware?.Dispose();
    }
}
