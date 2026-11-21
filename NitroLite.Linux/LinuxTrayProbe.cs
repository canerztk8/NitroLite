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
    /// True only when a host answers yes. When the question cannot be asked (no busctl, no session bus, a timeout) the
    /// answer is no: wrongly assuming no tray turns close-to-tray into a minimise, which is harmless, while wrongly
    /// assuming one hides the window on close with no icon to bring it back — the very bug this probe exists to stop.
    /// </summary>
    /// <remarks>It runs while the window is being built, so the question gets two seconds, not more.</remarks>
    public static bool IsAvailable(ICommandRunner? runner = null)
    {
        runner ??= ProcessCommandRunner.Instance;
        if (runner.Find("busctl") is not { } busctl) return false;

        var answer = runner.Run(busctl, Query, TimeSpan.FromSeconds(2));
        return answer.Ok && answer.Output.Trim().Equals("b true", StringComparison.Ordinal);
    }
}
