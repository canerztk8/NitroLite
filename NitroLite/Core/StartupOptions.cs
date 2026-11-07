using System;
using System.Linq;

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
}
