using Avalonia;

namespace NitroLite.Desktop;

/// <summary>
/// The entry point. Kept to the two lines Avalonia needs so the composition root stays in <see cref="AppHost"/>,
/// where it can be read without knowing anything about the UI framework.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Also used by the Avalonia designer, which is why it is public and parameterless.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
