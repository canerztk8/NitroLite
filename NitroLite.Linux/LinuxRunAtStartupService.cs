using System.Text;
using NitroLite.Core;

namespace NitroLite.Linux;

/// <summary>
/// "Start with the session" on Linux: an XDG autostart entry, <c>~/.config/autostart/nitrolite.desktop</c>, which
/// GNOME, KDE, Xfce, Cinnamon and the other desktops that follow the freedesktop autostart spec run at login.
/// </summary>
/// <remarks>
/// <para>
/// The entry runs the executable this process was started from, with <c>--autostart</c>, and with <c>--minimized</c>
/// while "start in the tray" is on. A desktop that shows no tray icons (stock GNOME without the AppIndicator extension)
/// cannot strand the app there: launching NitroLite again from the menu brings the running window forward.
/// </para>
/// <para>
/// Disabling deletes the entry rather than writing <c>Hidden=true</c>, so nothing is left behind after an uninstall.
/// An entry the user disabled through their desktop's own startup settings reads as disabled.
/// </para>
/// </remarks>
public sealed class LinuxRunAtStartupService : IRunAtStartupService
{
    public const string FileName = "nitrolite.desktop";

    private readonly string _directory;
    private readonly string? _executable;

    /// <param name="autostartDirectory">Left null, <c>$XDG_CONFIG_HOME/autostart</c> or <c>~/.config/autostart</c>.</param>
    /// <param name="executable">Left null, the path this process was started from.</param>
    public LinuxRunAtStartupService(string? autostartDirectory = null, string? executable = null)
    {
        _directory = autostartDirectory ?? DefaultDirectory();
        _executable = executable ?? Environment.ProcessPath;
    }

    public string EntryPath => Path.Combine(_directory, FileName);

    /// <summary>Supported wherever the process knows its own path, which is everywhere but an unusual host.</summary>
    public bool IsSupported => !string.IsNullOrEmpty(_executable);

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(EntryPath)) return Task.FromResult(false);

            foreach (var raw in File.ReadLines(EntryPath))
            {
                var line = raw.Trim();
                if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(false);
                }
            }

            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> SetEnabledAsync(bool enabled, bool startMinimized, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return Task.FromResult(false);

        try
        {
            if (!enabled)
            {
                if (File.Exists(EntryPath)) File.Delete(EntryPath);
                return Task.FromResult(true);
            }

            Directory.CreateDirectory(_directory);

            // Written next to the target and moved over it, so a session that starts while this runs never reads half
            // an entry.
            var temp = EntryPath + ".tmp";
            File.WriteAllText(temp, BuildEntry(_executable!, startMinimized), new UTF8Encoding(false));
            File.Move(temp, EntryPath, overwrite: true);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("The autostart entry could not be written", ex.Message);
            return Task.FromResult(false);
        }
    }

    /// <summary>The desktop entry. The path is quoted the way the desktop entry spec asks for an argument with spaces.</summary>
    public static string BuildEntry(string executable, bool startMinimized)
    {
        var exec = Quote(executable) + " --autostart" + (startMinimized ? " --minimized" : "");
        return
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=NitroLite\n" +
            "Comment=Fan, power mode and keyboard lighting control for Acer Nitro and Predator laptops\n" +
            $"Exec={exec}\n" +
            "Icon=nitrolite\n" +
            "Terminal=false\n" +
            "X-GNOME-Autostart-enabled=true\n" +
            "X-GNOME-Autostart-Delay=3\n";
    }

    /// <summary>
    /// A desktop-entry argument: quoted, with the four characters the spec reserves inside quotes escaped.
    /// </summary>
    public static string Quote(string argument)
    {
        var escaped = new StringBuilder(argument.Length + 2).Append('"');
        foreach (var c in argument)
        {
            if (c is '"' or '`' or '$' or '\\') escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.Append('"').ToString();
    }

    private static string DefaultDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathRooted(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "autostart");
    }
}
