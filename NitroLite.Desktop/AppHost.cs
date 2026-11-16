using System.IO;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using NitroLite.Desktop.ViewModels;
using NitroLite.Linux;

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
/// Nothing here writes to the hardware. The probe only reads, and the controller is created with the settings object
/// the rest of the app uses so the read-back counters survive a restart.
/// </para>
/// </remarks>
public sealed class AppHost : IDisposable
{
    private readonly IHardwareController? _hardware;

    private AppHost(AppSettings settings, IHardwareController? hardware, LinuxCapabilities? linux, string settingsPath)
    {
        _hardware = hardware;
        ViewModel = new MainViewModel(settings, hardware, linux, settingsPath);
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

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var controller = LinuxSysfsHardwareController.Create(settings);
                linux = controller.Capabilities;
                hardware = controller;
                AppLog.Info("Linux sysfs arka ucu bağlandı",
                    $"{linux.Tier} · {linux.Identity.ModelCode} · udev={(linux.NeedsUdevRule ? "gerekli" : "tamam")}");
            }
            catch (Exception ex)
            {
                // A probe that throws is a machine with no Acer support, not a reason to refuse to start.
                AppLog.Error("Linux donanım arka ucu kurulamadı", ex.ToString());
            }
        }

        return new AppHost(settings, hardware, linux, settingsPath);
    }

    public void Dispose()
    {
        ViewModel.Dispose();
        _hardware?.Dispose();
    }
}
