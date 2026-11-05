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

            if (arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase))
            {
                options.IsAutoStart = true;
                options.StartMinimized = true;
            }
            else if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
            {
                options.StartMinimized = true;
            }
        }

        return options;
    }
}
