using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NitroLite.Core;
using NitroLite.Desktop.Themes;
using NitroLite.Desktop.ViewModels;
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
    /// How many unhandled UI exceptions inside <see cref="UiExceptionWindow"/> are survived before the process gives
    /// up. A control that throws on every layout pass would otherwise spin the dispatcher forever.
    /// </summary>
    private const int MaxSurvivedUiExceptions = 5;

    /// <summary>
    /// The window the limit above is counted over. A throwing control hits the limit within a second; a tray app that
    /// runs for days and meets six unrelated exceptions in that time is not spinning, and used to be shut down anyway
    /// because the count never went back down.
    /// </summary>
    private static readonly TimeSpan UiExceptionWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the NitroSense sweep runs after a key press. Long enough to catch a launch the Acer service delays,
    /// short enough that a press does not leave a worker running.
    /// </summary>
    private static readonly TimeSpan NitroSenseSuppressionWindow = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long a shutdown path waits for the fans to be handed back. Bounded because the alternative is a machine
    /// that will not finish shutting down: the handback is worth a few seconds, not an indefinite wait.
    /// </summary>
    /// <remarks>
    /// Strictly larger than the sum of the hand-back's own two bounds — the curve loop's step in flight and the write
    /// gate — which are one second each. It used to equal the gate wait alone, so a contended gate consumed the whole
    /// budget and the Auto write was never reached: the machine exited with the EC still holding a fixed speed, which
    /// is the one failure these handlers exist to prevent.
    /// </remarks>
    private static readonly TimeSpan ShutdownHandbackTimeout = TimeSpan.FromSeconds(5);

    /// <summary>When each recent UI exception arrived, oldest first. Only entries inside the window are kept.</summary>
    private readonly Queue<DateTime> _recentUiExceptions = new();

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

            // The fan loop and the shutdown hand-back finish on pool threads, and the controls they update belong to
            // this one. Posted rather than invoked when called off the UI thread, so a hand-back running while the UI
            // thread waits for it cannot deadlock.
            _host.ViewModel.UiDispatch = action =>
            {
                if (Dispatcher.UIThread.CheckAccess()) action();
                else Dispatcher.UIThread.Post(action);
            };

            // A language change is the one setting that cannot be applied to a live window: the catalogues are read
            // through a static, and half the shell — the tray menu, the window title, the OSD — is built once. The
            // shell restarts itself instead, and the replacement is started here rather than in the view model so the
            // view model stays free of process handling and a test can assert the request without spawning one.
            _host.ViewModel.RestartRequest = () => RestartAsync(desktop);

            // A copy started in a second user session (fast user switching) shares the fans and the EC with the copy in
            // the first session, which owns the machine-wide slot. This one shows the machine but does not write to it.
            // Set before the window exists, so the first read already knows.
            if (SingleInstance is { OwnsMachineWide: false }) _host.ViewModel.HardwareWritesAllowed = false;

            // The laptop's own Nitro key. Subscribed before Start so a press during startup is not lost. The gate reads
            // the setting at press time, which is what makes turning the switch off take effect without a restart.
            //
            // Started on the thread pool, not here: on Windows the listener's first step enumerates every class in
            // root\WMI to find the Acer event class, and this method runs on the UI thread before the window or the
            // tray exist. A machine whose WMI repository is slow kept the whole app invisible for that long. A switch
            // that is off does not start it at all; turning it on starts it then (OnViewModelPropertyChanged).
            _nitroKey = _host.NitroKey;
            if (_nitroKey != null)
            {
                _nitroGate = new NitroKeyActivationGate(() => _host?.ViewModel.NitroKeyEnabled == true, ShowWindow);
                _nitroKey.NitroKeyPressed += OnNitroKeyPressed;
                // Windows can stop and start the listener again; the Linux one closes its devices on Stop.
                _nitroKeySwitch = new NitroKeyListenerSwitch(_nitroKey, canRestart: OperatingSystem.IsWindows());
                _nitroKeySwitch.Apply(_host.ViewModel.NitroKeyEnabled);
            }

            // On a model whose button the Acer service stack owns, NitroSense coming to the front is the only signal
            // the key produces. The watcher reports it; the decision to close NitroSense and come forward is made
            // below, where the setting and the window are.
            _nitroSense = _host.NitroSense;
            bool watchNitroSense = _nitroSense != null && _nitroSense.IsSupported;
            if (watchNitroSense) _nitroSense!.NitroSenseActivated += OnNitroSenseActivated;

            // The watcher is a 400 ms foreground poll whose only action is the redirect, so it runs only while the two
            // switches that ask for that redirect are on, and the key listener starts when its switch is turned on. The
            // subscription is what makes either switch take effect without a restart.
            if (watchNitroSense || _nitroKey != null) _host.ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (watchNitroSense) SyncNitroSenseWatcher();

            // The firmware may reset the Custom fan speed and the keyboard lighting while the machine sleeps, and the
            // fan loop only writes when its target changes. The view model writes both again on resume. A static event,
            // so it is removed in DisposeHost or it would pin this App for the life of the process. Linux has no resume
            // seam yet; the loop's own gap detection still keeps a stale reading from handing the fans back there.
            SubscribeToResume();

            _window = new MainWindow { DataContext = _host.ViewModel };

            // "Start in the tray" is a stored preference, and the sign-in entry carries --minimized while it is on.
            // Either one means the window must not appear: a launch at sign-in that opened a window is the opposite
            // of what the switch promises.
            bool startHidden = StartupOptions.ShouldStartHidden(
                StartupOptions.Parse(Environment.GetCommandLineArgs()), _host.ViewModel.StartMinimizedOn)
                // A desktop with no tray would leave a hidden window unreachable, so it opens instead.
                && DesktopShell.TrayAvailable;

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

#if WINDOWS
            // A windowed start that is still alive ten seconds later confirms an installed update, so the previous build
            // (.old) may be swept. Both start paths reach this line, the tray start included; the CLI never does.
            if (OperatingSystem.IsWindows())
            {
                _ = Task.Delay(TimeSpan.FromSeconds(10))
                    .ContinueWith(_ => UpdateService.MarkStartupHealthy(), TaskScheduler.Default);
            }
#endif

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
    /// Starts a replacement copy and then exits through the clean path, so the fans are handed back exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is the whole trick. The replacement is started first because a process cannot start its successor
    /// after it has gone; it is given <see cref="Program.RestartArgument"/> so it waits for the single-instance slot
    /// instead of treating the wait as a duplicate launch. If the start fails — a folder that has been deleted, a
    /// policy that refuses the launch — the app stays open and says so, because exiting would leave the user with
    /// nothing.
    /// </para>
    /// <para>
    /// The exit is the same <see cref="ExitAsync"/> the tray menu uses, so the fan handback, the tray icon and the
    /// host disposal all run once and in the order they already run in.
    /// </para>
    /// </remarks>
    private async Task RestartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting) return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            AppLog.Warn("No executable was found to restart");
            return;
        }

        try
        {
            using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                Arguments = Program.RestartArgument,
                UseShellExecute = true
            });

            if (started == null)
            {
                AppLog.Warn("The restart could not be started");
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The restart could not be started", ex.Message);
            return;
        }

        await ExitAsync(desktop).ConfigureAwait(false);
    }

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
            AppLog.Warn("Error during clean exit", ex.Message);
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
            AppLog.Warn("Fans could not be restored at session end", ex.Message);
        }
        finally
        {
            DisposeHost();
        }
    }

    /// <summary>
    /// The process is being torn down. There is no time for a clean exit, so the fans get one best-effort handback.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e) => HandBackFansBestEffort("process exit");

    /// <summary>
    /// An exception escaped every handler and the process is about to die with the fans at whatever speed the last
    /// write left them at.
    /// </summary>
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled exception", (e.ExceptionObject as Exception)?.ToString() ?? "unknown");
        HandBackFansBestEffort("unhandled exception");
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
            AppLog.Warn("The fans could not be handed back to automatic mode", reason);
        }
        catch (Exception ex)
        {
            AppLog.Warn("The fans could not be handed back to automatic mode", $"{reason}: {ex.Message}");
        }
    }

    private void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("UI exception", e.Exception?.ToString() ?? "unknown");

        // Raised on the UI thread only, so the queue needs no lock.
        var now = DateTime.UtcNow;
        while (_recentUiExceptions.Count > 0 && now - _recentUiExceptions.Peek() > UiExceptionWindow)
            _recentUiExceptions.Dequeue();
        _recentUiExceptions.Enqueue(now);

        if (_recentUiExceptions.Count <= MaxSurvivedUiExceptions)
        {
            // Handled: the window stays open and the user can keep working. The windowed count is what stops a control
            // that throws on every layout pass from spinning the dispatcher forever.
            e.Handled = true;
            return;
        }

        AppLog.Error("The UI exception limit was exceeded", "The application is shutting down.");
        e.Handled = false;
    }

    /// <summary>
    /// Starts or stops the NitroSense watcher to match the two switches it serves.
    /// </summary>
    /// <remarks>
    /// With either switch off the watcher has nothing to do, and a timer that wakes the CPU two and a half times a
    /// second to decide that is the kind of idle cost this app exists to avoid. Both setters raise their own name, so
    /// the switch takes effect the moment it is flipped.
    /// </remarks>
    private void SyncNitroSenseWatcher()
    {
        if (_nitroSense == null) return;

        if (_host?.ViewModel is { NitroKeyEnabled: true, RedirectNitroSenseEnabled: true }) _nitroSense.Start();
        else _nitroSense.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.NitroKeyEnabled) or nameof(MainViewModel.RedirectNitroSenseEnabled))
            SyncNitroSenseWatcher();

        if (e.PropertyName == nameof(MainViewModel.NitroKeyEnabled) && _host != null)
            _nitroKeySwitch?.Apply(_host.ViewModel.NitroKeyEnabled);
    }

    /// <summary>Starts and stops the key listener with its setting; created with the listener.</summary>
    private NitroKeyListenerSwitch? _nitroKeySwitch;

#if WINDOWS
    /// <summary>True while this App is subscribed to the operating system's power events.</summary>
    private bool _resumeSubscribed;
#endif

    private void SubscribeToResume()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows() || _resumeSubscribed) return;

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _resumeSubscribed = true;
        }
        catch (Exception ex)
        {
            // Without the event the app still works as before: the lighting comes back when the window does.
            AppLog.Warn("The resume event could not be subscribed to", ex.Message);
        }
#endif
    }

    private void UnsubscribeFromResume()
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows() || !_resumeSubscribed) return;

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            AppLog.Warn("The resume event could not be unsubscribed from", ex.Message);
        }

        _resumeSubscribed = false;
#endif
    }

#if WINDOWS
    /// <summary>
    /// Raised on the system-events thread. Only a resume matters here; the hop to the UI thread is explicit because
    /// the view model's controls belong to it.
    /// </summary>
    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;

        Dispatcher.UIThread.Post(() =>
        {
            var viewModel = _host?.ViewModel;
            if (viewModel != null) _ = viewModel.OnSystemResumedAsync();
        });
    }
#endif

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

        // A process-wide static event, so it pins this App for the life of the process unless it is removed. Left
        // armed after the host is gone, it would also swallow a UI exception raised during shutdown and count it
        // against a limit nobody is watching any more.
        Dispatcher.UIThread.UnhandledException -= OnUiException;

        // The power event is process-wide too, and a resume after the host is gone would reach a disposed view model.
        UnsubscribeFromResume();

        // Unsubscribed before the host is disposed: a switch flipped during teardown would otherwise reach a view
        // model that is already gone.
        if (_host != null) _host.ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // The NitroSense watcher is torn down on its own, not inside the key-listener branch below. It used to be
        // nested there, which meant a machine whose key listener was null — a platform without the Acer input device,
        // or a composition root that threw between the two constructions — left a 400 ms foreground poll running with
        // no owner and an event subscription pointing at a disposed view model.
        if (_nitroSense != null)
        {
            _nitroSense.NitroSenseActivated -= OnNitroSenseActivated;
            _nitroSense.Stop();
            _nitroSense.Dispose();
            _nitroSense = null;
        }

        // Unsubscribed before the host is disposed: the listener's read loop can raise one last event while it is
        // shutting down, and a handler that reached a disposed view model would throw on a background thread.
        if (_nitroKey != null)
        {
            _nitroKey.NitroKeyPressed -= OnNitroKeyPressed;
            _nitroKey.Stop();
            _nitroKey = null;
        }

        _nitroGate = null;

        // The icon is removed from the Application's own collection before it is disposed. Avalonia keeps tray icons
        // in an attached collection on the Application, so disposing the icon alone leaves a disposed object in a
        // list that outlives it.
        if (_tray != null)
        {
            var icons = TrayIcon.GetIcons(this);
            icons?.Remove(_tray.Icon);
            _tray.Dispose();
            _tray = null;
        }

        _host?.Dispose();
        _host = null;
    }
}
