using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;
using NitroLite.Linux.Sysfs;

namespace NitroLite.Desktop;

/// <summary>
/// The composition root: settings, language, the hardware backend and the view model, wired once at startup.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place that decides which backend the app talks to. On Linux it is
/// <see cref="LinuxSysfsHardwareController"/>, built from a <see cref="LinuxCapabilityProbe"/> pass over the real
/// <c>/sys</c> tree. On anything else there is no backend at all, and the window says so: the Windows hardware layer
/// lives in the WPF project and is deliberately not referenced here, because referencing it would drag
/// <c>System.Management</c> and the registry into a build that has to run on a Linux desktop.
/// </para>
/// <para>
/// It is also the only place that decides which platform services exist. Game detection gets the <c>/proc</c> scanner,
/// school mode gets the sysfs brightness and boost backends, and both get the same settings object the rest of the app
/// uses — so a capability the probe found and a capability the service writes can never disagree.
/// </para>
/// <para>
/// Nothing here writes to the hardware. The probe only reads, and the controller is created with the settings object
/// the rest of the app uses so the read-back counters survive a restart.
/// </para>
/// </remarks>
public sealed class AppHost : IDisposable
{
    private readonly IHardwareController? _hardware;

    private AppHost(
        AppSettings settings,
        IHardwareController? hardware,
        LinuxCapabilities? linux,
        string settingsPath,
        GameProfileService? gameProfiles,
        SchoolModeService? schoolMode,
        RgbProfileService rgbProfiles)
    {
        _hardware = hardware;
        ViewModel = new MainViewModel(
            settings, hardware, linux, settingsPath, gameProfiles, schoolMode, rgbProfiles);
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Builds everything. Never throws: a machine with no Acer support gets a window that explains it.</summary>
    public static AppHost Create()
    {
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
        LinuxCapabilities? linux = null;
        GameProfileService? gameProfiles = null;
        SchoolModeService? schoolMode = null;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var controller = LinuxSysfsHardwareController.Create(settings);
                linux = controller.Capabilities;
                hardware = controller;
                AppLog.Info("Linux sysfs arka ucu bağlandı",
                    $"{linux.Tier} · {linux.Identity.ModelCode} · udev={(linux.NeedsUdevRule ? "gerekli" : "tamam")}");

                var fs = RealSysfsFileSystem.Instance;
                gameProfiles = CreateGameProfiles(settings, controller, fs);
                schoolMode = CreateSchoolMode(settings, controller, linux, fs);
            }
            catch (Exception ex)
            {
                // A probe that throws is a machine with no Acer support, not a reason to refuse to start.
                AppLog.Error("Linux donanım arka ucu kurulamadı", ex.ToString());
            }
        }

        return new AppHost(settings, hardware, linux, settingsPath, gameProfiles, schoolMode, new RgbProfileService());
    }

    /// <summary>
    /// Game detection over <c>/proc</c>. The power state is supplied so a Turbo profile is clamped to Balanced on
    /// battery instead of being written and silently refused by the firmware.
    /// </summary>
    private static GameProfileService CreateGameProfiles(
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
    private static SchoolModeService CreateSchoolMode(
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

    public void Dispose()
    {
        ViewModel.Dispose();
        _hardware?.Dispose();
    }
}
