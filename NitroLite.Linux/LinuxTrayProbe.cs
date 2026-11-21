namespace NitroLite.Linux;

/// <summary>
/// Whether the desktop shows tray icons: a StatusNotifier host owns <c>org.kde.StatusNotifierWatcher</c> on the
/// session bus. KDE, Cinnamon, Xfce, MATE and Budgie have one; GNOME has one only with the AppIndicator extension.
/// </summary>
public static class LinuxTrayProbe
{
    private static readonly string[] Query =
    {
        "--user", "call", "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
        "NameHasOwner", "s", "org.kde.StatusNotifierWatcher"
    };

    /// <summary>
    /// True when a host answers, and also when the question cannot be asked (no busctl): assuming a tray that is
    /// there costs nothing, while assuming none would turn a working close-to-tray into a minimise for no reason.
    /// </summary>
    public static bool IsAvailable(ICommandRunner? runner = null)
    {
        runner ??= ProcessCommandRunner.Instance;
        if (runner.Find("busctl") is not { } busctl) return true;

        var answer = runner.Run(busctl, Query, TimeSpan.FromSeconds(3));
        if (!answer.Ok) return true;

        return !answer.Output.Trim().Equals("b false", StringComparison.Ordinal);
    }
}
