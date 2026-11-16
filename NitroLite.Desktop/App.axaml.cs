using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NitroLite.Core;
using NitroLite.Desktop.Themes;
using NitroLite.Desktop.Views;

namespace NitroLite.Desktop;

public partial class App : Application
{
    private AppHost? _host;
    private TrayIconHost? _tray;
    private bool _exiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // A palette has to exist before the first control is created, or every DynamicResource in the style layer
        // resolves to nothing and the window opens unstyled. The stored choice replaces this one in the view model's
        // constructor, which runs before the window is built.
        DesktopThemeManager.Instance.Apply(AppTheme.Dark);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The composition root runs before the window exists, so a machine with no Acer support still opens a
            // window that explains why instead of failing to start.
            _host = AppHost.Create();
            var window = new MainWindow { DataContext = _host.ViewModel };
            desktop.MainWindow = window;

            // Hiding the window must not end the process: the telemetry timer and the fan curve keep running while
            // the app sits in the tray. Only the clean exit below shuts it down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _tray = new TrayIconHost(_host.ViewModel, ShowWindow, () => _ = ExitAsync(window, desktop));

            // Avalonia keeps the tray icons in an attached collection on the Application, not in a property of its
            // own, so the icon is registered through TrayIcon.SetIcons rather than added to a list here.
            var icons = TrayIcon.GetIcons(this) ?? new TrayIcons();
            icons.Add(_tray.Icon);
            TrayIcon.SetIcons(this, icons);

            desktop.ShutdownRequested += (_, _) => DisposeHost();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Brings the window back from the tray, restoring it if it was minimised before it was hidden.</summary>
    private void ShowWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not { } window) return;

        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// The only path that ends the process. The fans are handed back to the firmware first: a machine left with a
    /// fixed fan speed written by a program that is no longer running is the one failure a tray menu can cause.
    /// </summary>
    private async Task ExitAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            if (_host != null) await _host.ViewModel.CleanExitAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Temiz çıkış sırasında hata", ex.Message);
        }
        finally
        {
            window.AllowClose = true;
            DisposeHost();
            desktop.Shutdown();
        }
    }

    private void DisposeHost()
    {
        _tray?.Dispose();
        _tray = null;
        _host?.Dispose();
        _host = null;
    }
}
