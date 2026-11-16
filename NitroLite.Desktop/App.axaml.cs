using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NitroLite.Core;
using NitroLite.Desktop.Themes;
using NitroLite.Desktop.Views;

namespace NitroLite.Desktop;

public partial class App : Application
{
    private AppHost? _host;
    private TrayIconHost? _tray;
    private MainWindow? _window;
    private bool _exiting;

    /// <summary>
    /// The single-instance guard the entry point acquired. Held here so a second launch can be turned into "show the
    /// window" rather than a second copy of the app.
    /// </summary>
    public static SingleInstanceGuard? SingleInstance { get; set; }

    /// <summary>
    /// How many unhandled UI exceptions in a row are survived before the process gives up. A control that throws on
    /// every layout pass would otherwise spin the dispatcher forever.
    /// </summary>
    private const int MaxSurvivedUiExceptions = 5;

    private int _survivedUiExceptions;

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
            // window that explains why instead of failing to start. The dialog service takes the window as a delegate
            // because the window does not exist yet.
            _host = AppHost.Create(new AvaloniaDialogService(() => _window));
            _window = new MainWindow { DataContext = _host.ViewModel };
            desktop.MainWindow = _window;

            // Hiding the window must not end the process: the telemetry timer and the fan curve keep running while
            // the app sits in the tray. Only the clean exit below shuts it down.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _tray = new TrayIconHost(_host.ViewModel, ShowWindow, () => _ = ExitAsync(desktop));

            // Avalonia keeps the tray icons in an attached collection on the Application, not in a property of its
            // own, so the icon is registered through TrayIcon.SetIcons rather than added to a list here.
            var icons = TrayIcon.GetIcons(this) ?? new TrayIcons();
            icons.Add(_tray.Icon);
            TrayIcon.SetIcons(this, icons);

            // A second launch asks the running window to come forward. The event arrives on a background thread, so
            // the hop to the UI thread is explicit.
            if (SingleInstance != null)
            {
                SingleInstance.ActivationRequested += () => Dispatcher.UIThread.Post(ShowWindow);
            }

            // A session that ends — sign-out, shutdown, a forced logoff — is not a clean exit, but the fans still have
            // to go back to the firmware. The handler is synchronous because the session does not wait for a task.
            desktop.ShutdownRequested += (_, _) => OnShutdownRequested();

            // One bad control must not take the window down with it. The exception is logged and the app keeps
            // running; a run of them means something is throwing on every pass and the process gives up instead of
            // spinning.
            Dispatcher.UIThread.UnhandledException += OnUiException;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Brings the window back from the tray, restoring it if it was minimised before it was hidden.</summary>
    private void ShowWindow()
    {
        if (_window == null) return;

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.RestoreFromTray();
    }

    /// <summary>
    /// The only path that ends the process. The fans are handed back to the firmware first: a machine left with a
    /// fixed fan speed written by a program that is no longer running is the one failure a tray menu can cause.
    /// </summary>
    private async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
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
            if (_window != null) _window.AllowClose = true;
            DisposeHost();
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// The session is ending and there is no time for a full clean exit. The fans are still handed back, because the
    /// alternative is a machine that boots with a fixed fan speed and no program to change it.
    /// </summary>
    private void OnShutdownRequested()
    {
        try
        {
            _host?.ViewModel.RestoreFansToAutoAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Oturum kapanışında fanlar iade edilemedi", ex.Message);
        }
        finally
        {
            DisposeHost();
        }
    }

    private void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Arayüz istisnası", e.Exception?.ToString() ?? "bilinmeyen");

        if (++_survivedUiExceptions <= MaxSurvivedUiExceptions)
        {
            // Handled: the window stays open and the user can keep working. The counter is what stops a control that
            // throws on every layout pass from spinning the dispatcher forever.
            e.Handled = true;
            return;
        }

        AppLog.Error("Arayüz istisnası sınırı aşıldı", "Uygulama kapatılıyor.");
        e.Handled = false;
    }

    private void DisposeHost()
    {
        _tray?.Dispose();
        _tray = null;
        _host?.Dispose();
        _host = null;
    }
}
