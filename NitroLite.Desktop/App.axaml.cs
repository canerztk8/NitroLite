using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NitroLite.Desktop.Views;

namespace NitroLite.Desktop;

public partial class App : Application
{
    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The composition root runs before the window exists, so a machine with no Acer support still opens a
            // window that explains why instead of failing to start.
            _host = AppHost.Create();
            desktop.MainWindow = new MainWindow { DataContext = _host.ViewModel };
            desktop.ShutdownRequested += (_, _) => _host?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
