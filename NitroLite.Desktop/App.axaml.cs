using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NitroLite.Core;
using NitroLite.Desktop.Themes;
using NitroLite.Desktop.Views;

namespace NitroLite.Desktop;

public partial class App : Application, IDisposable
{
    private AppHost? _host;
    private TrayIconHost? _tray;
    private MainWindow? _window;
    private INitroKeyListener? _nitroKey;
    private NitroKeyActivationGate? _nitroGate;
    private INitroSenseWatcher? _nitroSense;
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

    /// <summary>
    /// How long the NitroSense sweep runs after a key press. Long enough to catch a launch the Acer service delays,
    /// short enough that a press does not leave a worker running.
    /// </summary>
    private static readonly TimeSpan NitroSenseSuppressionWindow = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long a shutdown path waits for the fans to be handed back. Bounded because the alternative is a machine
    /// that will not finish shutting down: the handback is worth two seconds, not an indefinite wait.
    /// </summary>
    private static readonly TimeSpan ShutdownHandbackTimeout = TimeSpan.FromSeconds(2);

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

            // The laptop's own Nitro key. Started before the window is built so the settings row's support check is
            // already answered when the binding first reads it, and subscribed before Start so a press during startup
            // is not lost. The gate reads the setting at press time, which is what makes turning the switch off take
            // effect without a restart.
            _nitroKey = _host.NitroKey;
            if (_nitroKey != null)
            {
                _nitroGate = new NitroKeyActivationGate(() => _host?.ViewModel.NitroKeyEnabled == true, ShowWindow);
                _nitroKey.NitroKeyPressed += OnNitroKeyPressed;
                _nitroKey.Start();
            }

            // On a model whose button the Acer service stack owns, NitroSense coming to the front is the only signal
            // the key produces. The watcher reports it; the decision to close NitroSense and come forward is made
            // below, where the setting and the window are.
            _nitroSense = _host.NitroSense;
            if (_nitroSense != null && _nitroSense.IsSupported)
            {
                _nitroSense.NitroSenseActivated += OnNitroSenseActivated;
                _nitroSense.Start();
            }

            _window = new MainWindow { DataContext = _host.ViewModel };

            // "Start in the tray" is a stored preference, and the sign-in entry carries --minimized while it is on.
            // Either one means the window must not appear: a launch at sign-in that opened a window is the opposite
            // of what the switch promises.
            bool startHidden = StartupOptions.ShouldStartHidden(
                StartupOptions.Parse(Environment.GetCommandLineArgs()), _host.ViewModel.StartMinimizedOn);

            if (startHidden)
            {
                // Not handed to the lifetime: it shows whatever it is given, and a window that appears for one frame
                // before hiding is exactly the flash the switch exists to avoid. The setup runs here instead, so the
                // telemetry loop and the fan curve are live while the window sits in the tray.
                _ = _window.InitializeOnceAsync();
            }
            else
            {
                desktop.MainWindow = _window;
            }

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

            // The lifetime's own exit event, for a shutdown that never went through the tray menu or a session end.
            // DisposeHost is idempotent, so the paths that already disposed the host are not affected.
            desktop.Exit += (_, _) => DisposeHost();

            // The two paths that never reach the clean exit: a crash, and a process that is being torn down. Both get
            // a best-effort handback, because a machine left with a fixed fan speed is the one failure worth
            // surviving a crash.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

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

        _window.BringToFront();
        _window.RestoreFromTray();
    }

    /// <summary>
    /// The Nitro key arrived on a background thread, so the hop to the UI thread is explicit. The gate reads the
    /// setting at press time, which is what makes turning the switch off take effect without a restart.
    /// </summary>
    private void OnNitroKeyPressed(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_nitroGate?.HandlePress() != true) return;

            // The Acer service launches NitroSense a moment after the key, so a single close races that launch and
            // the window ends up behind it. The sweep runs for a few seconds, and the window is raised again once it
            // is done because NitroSense can take the foreground on its way out.
            //
            // Gated on the redirect switch, which is what "the key opens NitroLite and not NitroSense" means. With it
            // off the key still raises the window; NitroSense is simply left alone.
            if (_host?.ViewModel.RedirectNitroSenseEnabled != true) return;

            _ = Task.Run(() =>
            {
                if (_nitroSense?.CloseFor(NitroSenseSuppressionWindow) == true)
                {
                    Dispatcher.UIThread.Post(ShowWindow);
                }
            });
        });

    /// <summary>
    /// NitroSense came to the front, which on this model means the Nitro key was pressed.
    /// </summary>
    /// <remarks>
    /// Acted on whenever the user asked for the redirect, whether or not NitroLite is already in front. The earlier
    /// version skipped the close while the window was visible, which is exactly the case that was reported: with
    /// NitroLite covering the screen the key opened NitroSense behind it and left it running. "The key opens NitroLite
    /// and not NitroSense" has to hold in both states, so the only gates left are the two switches.
    /// </remarks>
    private void OnNitroSenseActivated(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_host?.ViewModel.NitroKeyEnabled != true) return;
            if (_host.ViewModel.RedirectNitroSenseEnabled != true) return;
            if (_window == null) return;

            // Closing waits on the process, so it does not belong on the UI thread.
            _ = Task.Run(() => _nitroSense?.Close());
            ShowWindow();
        });

    /// <summary>
    /// The only path that ends the process. The fans are handed back to the firmware first: a machine left with a
    /// fixed fan speed written by a program that is no longer running is the one failure a tray menu can cause.
    /// </summary>
    /// <remarks>
    /// The handback is awaited without capturing the UI thread, and the window and the lifetime are touched back on
    /// it. Awaiting with the context captured would deadlock the moment this is called from the UI thread, because
    /// the continuation would be queued behind the very frame that is waiting for it.
    /// </remarks>
    private async Task ExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            if (_host != null) await _host.ViewModel.CleanExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Temiz çıkış sırasında hata"), ex.Message);
        }
        finally
        {
            // Posted rather than awaited: the window and the lifetime are UI-thread objects, and a post cannot
            // deadlock the thread that is shutting them down.
            Dispatcher.UIThread.Post(() =>
            {
                if (_window != null) _window.AllowClose = true;
                DisposeHost();
                desktop.Shutdown();
            });
        }
    }

    /// <summary>
    /// The session is ending and there is no time for a full clean exit. The fans are still handed back, because the
    /// alternative is a machine that boots with a fixed fan speed and no program to change it.
    /// </summary>
    /// <remarks>
    /// The handback runs on a thread-pool thread with a bound. The old version called
    /// <c>GetAwaiter().GetResult()</c> on the UI thread, which blocks the thread the continuation needs: a deadlock,
    /// not a handback, and the fans stayed where they were.
    /// </remarks>
    private void OnShutdownRequested()
    {
        try
        {
            _host?.ViewModel.RestoreFansToAutoBlocking(ShutdownHandbackTimeout);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Oturum kapanışında fanlar iade edilemedi"), ex.Message);
        }
        finally
        {
            DisposeHost();
        }
    }

    /// <summary>
    /// The process is being torn down. There is no time for a clean exit, so the fans get one best-effort handback.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e) => HandBackFansBestEffort("süreç çıkışı");

    /// <summary>
    /// An exception escaped every handler and the process is about to die with the fans at whatever speed the last
    /// write left them at.
    /// </summary>
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error(Loc.T("Yakalanmamış istisna"), (e.ExceptionObject as Exception)?.ToString() ?? "bilinmeyen");
        HandBackFansBestEffort("yakalanmamış istisna");
    }

    /// <summary>
    /// One bounded attempt to put the fans back under firmware control, for the paths that cannot await. Never
    /// throws: it runs while the process is already failing, and a second failure there helps nobody.
    /// </summary>
    private void HandBackFansBestEffort(string reason)
    {
        try
        {
            if (_host?.ViewModel.RestoreFansToAutoBlocking(ShutdownHandbackTimeout) == true) return;
            AppLog.Warn("Fanlar otomatik moda iade edilemedi", reason);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Fanlar otomatik moda iade edilemedi", $"{reason}: {ex.Message}");
        }
    }

    private void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error(Loc.T("Arayüz istisnası"), e.Exception?.ToString() ?? "bilinmeyen");

        if (++_survivedUiExceptions <= MaxSurvivedUiExceptions)
        {
            // Handled: the window stays open and the user can keep working. The counter is what stops a control that
            // throws on every layout pass from spinning the dispatcher forever.
            e.Handled = true;
            return;
        }

        AppLog.Error(Loc.T("Arayüz istisnası sınırı aşıldı"), "Uygulama kapatılıyor.");
        e.Handled = false;
    }

    /// <summary>
    /// The framework's own teardown hook. <see cref="ExitAsync"/> and the session-ending handler both dispose the
    /// host already; this covers a lifetime that is shut down without going through either of them.
    /// </summary>
    public void Dispose()
    {
        DisposeHost();

        // No finalizer on this type or on the host it owns, so there is nothing for the collector to suppress.
        GC.SuppressFinalize(this);
    }

    private void DisposeHost()
    {
        // Unsubscribed first: the process-exit and crash handlers reach the view model, and a handler that ran after
        // the host was disposed would write to a disposed gate.
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

        // Unsubscribed before the host is disposed: the listener's read loop can raise one last event while it is
        // shutting down, and a handler that reached a disposed view model would throw on a background thread.
        if (_nitroKey != null)
        {
            _nitroKey.NitroKeyPressed -= OnNitroKeyPressed;

            if (_nitroSense != null)
            {
                _nitroSense.NitroSenseActivated -= OnNitroSenseActivated;
                _nitroSense.Stop();
                _nitroSense.Dispose();
                _nitroSense = null;
            }
            _nitroKey.Stop();
            _nitroKey = null;
        }

        _nitroGate = null;

        _tray?.Dispose();
        _tray = null;
        _host?.Dispose();
        _host = null;
    }
}
