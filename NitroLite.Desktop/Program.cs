using Avalonia;
using NitroLite.Core;

#if WINDOWS
using NitroLite.Core.Hardware.Wmi;
#endif

namespace NitroLite.Desktop;

/// <summary>
/// The entry point. Kept to the two lines Avalonia needs so the composition root stays in <see cref="AppHost"/>,
/// where it can be read without knowing anything about the UI framework.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if WINDOWS
        // The elevated WMI helper is a second process of this same executable, started with Verb = "runas". It is
        // decided before Avalonia is touched: a worker that started a window would put a second NitroLite on the
        // user's desktop, and the caller would be left waiting on a pipe nobody is listening to.
        var mode = StartupMode.Parse(args);
        if (mode.IsWmiWorker)
        {
            Environment.Exit(WmiWorkerHost.Run(mode));
            return;
        }
#endif

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia designer, which is why it is public and parameterless.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
