
namespace NitroLite.Core;

public class StartupOptions
{
    public bool IsAutoStart { get; set; }
    public bool StartMinimized { get; set; }

    public static StartupOptions Parse(string[]? args)
    {
        var options = new StartupOptions();
        if (args == null || args.Length == 0)
        {
            return options;
        }

        foreach (var rawArg in args)
        {
            if (string.IsNullOrWhiteSpace(rawArg)) continue;
            var arg = rawArg.Trim();

            // --autostart only marks a Windows sign-in launch. Whether the window stays hidden is decided by
            // --minimized, which the Run entry carries only while "start in the tray" is checked.
            if (arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase))
            {
                options.IsAutoStart = true;
            }
            else if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
            {
                options.StartMinimized = true;
            }
        }

        return options;
    }

    /// <summary>
    /// True when the window must not appear at launch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flag is what the sign-in entry carries while "start in the tray" is checked, and the switch rewrites the
    /// registered command line when it changes, so the two cannot disagree.
    /// </para>
    /// <para>
    /// The stored preference is consulted as well, but only for a sign-in launch. A shortcut created before the flag
    /// existed would otherwise open a window on a machine whose owner asked for the tray, while double-clicking the
    /// icon still always shows the window — a launch that appears to do nothing is the one complaint this rule must
    /// not create.
    /// </para>
    /// </remarks>
    public static bool ShouldStartHidden(StartupOptions? options, bool startInTrayPreference) =>
        options?.StartMinimized == true || (options?.IsAutoStart == true && startInTrayPreference);
}
