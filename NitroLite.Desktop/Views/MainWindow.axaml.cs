using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;

namespace NitroLite.Desktop.Views;

/// <summary>
/// The shell window: header, rail, the five pages, and the telemetry timer.
/// </summary>
/// <remarks>
/// <para>
/// The one-second loop lives here rather than in <see cref="MainViewModel"/> because the view model deliberately has no
/// Avalonia types in it — that is what lets it be constructed and asserted on in a unit test with no display attached.
/// The timer stops when the window is really closed, so a closed window stops reading <c>/sys</c> instead of polling a
/// machine nobody is looking at.
/// </para>
/// <para>
/// Closing the window hides it instead of ending the process. That is what keeps a custom fan curve running while the
/// user is in a game: the curve is driven by the telemetry timer, and a timer that stops on close would stop exactly
/// when the machine gets hot. The process ends only through the tray's "Temiz Çıkış", which hands the fans back to the
/// firmware first and then sets <see cref="AllowClose"/>.
/// </para>
/// <para>
/// The window remembers where it was. A user who moved it to the second monitor should not have to move it again on
/// every launch, and the WPF window stored the same four numbers.
/// </para>
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>The polling interval while the window is on screen.</summary>
    private static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The polling interval while the window is in the tray. Slower, not stopped: the fan curve still has to follow the
    /// temperature, but a hidden window does not need a sample every second.
    /// </summary>
    private static readonly TimeSpan HiddenInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The polling interval while the overlay is on screen. Twice the visible rate: an OSD that lags the game by a
    /// second is worse than no OSD, and the extra reads are cheap next to a frame.
    /// </summary>
    private static readonly TimeSpan OsdInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer;
    private MainViewModel? _viewModel;

    /// <summary>
    /// The rail's column. Its width is written here rather than bound, because a <see cref="ColumnDefinition"/> is an
    /// <see cref="AvaloniaObject"/> with no data context: a binding on it never resolves and the rail silently falls
    /// back to the width of its own content, which is what made the splitter look like it did nothing.
    /// </summary>
    private ColumnDefinition? _railColumn;

    /// <summary>
    /// Collapses a rail drag into one settings write. The splitter raises a width change per pixel, and the view model
    /// saves the file on every assignment.
    /// </summary>
    private readonly DispatcherTimer _railSaveTimer;

    /// <summary>
    /// Takes the banner down again. The WPF window kept the same timer: a message that never leaves is a message that
    /// sits on top of the page it is reporting about.
    /// </summary>
    private readonly DispatcherTimer _toastTimer;

    /// <summary>The in-game overlay, created with the window and shown only while the setting is on.</summary>
    private OsdOverlayWindow? _osd;

    /// <summary>
    /// Guards the one-shot setup in the <see cref="Window.Opened"/> handler.
    /// </summary>
    /// <remarks>
    /// Closing the window only hides it, so <see cref="Window.Opened"/> fires again on every return from the tray.
    /// Without this guard each return subscribed the same handlers a second time, built a second
    /// <see cref="OsdOverlayWindow"/> that nothing ever closed, and re-ran the game-session recovery — which wrote the
    /// pre-game mode and fan back over a game that was still running. It is a gate rather than a flag because a tray
    /// launch calls the setup directly and the window's own <c>Opened</c> can follow it.
    /// </remarks>
    private readonly OneShotGate _initialized = new();

    /// <summary>
    /// The last size and position the window had while it was not maximised. Avalonia has no <c>RestoreBounds</c>, so
    /// the numbers are captured on every move and resize and written back when the window is restored.
    /// </summary>
    private PixelPoint _normalPosition;
    private Size _normalSize;
    private bool _hasNormalBounds;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = VisibleInterval };
        _timer.Tick += async (_, _) => await TickSafelyAsync();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) => HideToast();

        _railSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _railSaveTimer.Tick += (_, _) => SaveRailWidth();

        Opened += async (_, _) => await InitializeOnceAsync();

        // Minimising is a state change, not a visibility change: IsVisible stays true, so nothing else would notice.
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            UpdateTelemetryPollingRate();
            UpdateChartVisibility();
        };

        PositionChanged += (_, _) => CaptureNormalBounds();
        SizeChanged += (_, _) => CaptureNormalBounds();

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _toastTimer.Stop();
            _railSaveTimer.Stop();
            if (_railColumn != null) _railColumn.PropertyChanged -= OnRailColumnChanged;
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _osd?.Close();
            _osd = null;
        };
    }

    /// <summary>
    /// The one-shot setup: the clipboard hook, the subscriptions, the window placement, the view model's first read
    /// and the overlay window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent, and called from two places. <see cref="Window.Opened"/> is the normal one, but a launch that starts
    /// in the tray never raises it — the window is never shown — so the shell calls this directly. Without that, a
    /// tray launch would have no telemetry loop and no fan curve, which is the opposite of what the tray is for.
    /// </para>
    /// <para>
    /// The guard is what makes the second call free. Closing the window only hides it, so <see cref="Window.Opened"/>
    /// fires again on every return from the tray; a second pass would subscribe the same handlers twice, build a
    /// second overlay window that nothing ever closes, and re-run the game-session recovery against a session that is
    /// still running.
    /// </para>
    /// </remarks>
    public async Task InitializeOnceAsync()
    {
        if (!_initialized.TryEnter()) return;

        try
        {
            // The clipboard is the only platform service the view model needs, and it is a delegate so a test can
            // leave it unset. Set it before the first command can run.
            ClipboardService.Setter = text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null) _ = CopyAsync(clipboard, text);
            };

            _viewModel = DataContext as MainViewModel;
            if (_viewModel == null) return;

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // The rail starts at the width the user left it at. An absolute width also decides how the splitter
            // behaves: with a pixel column next to a star column it resizes the pixel one, so the rail keeps a real
            // width instead of turning into a star that the page can squeeze.
            _railColumn = RailGrid.ColumnDefinitions[0];
            _railColumn.Width = new GridLength(_viewModel.SidebarWidth);
            _railColumn.PropertyChanged += OnRailColumnChanged;

            RestoreWindowPosition();
            await _viewModel.InitializeAsync();

            // The overlay is a second window bound to the same view model, so the two can never disagree about a
            // reading. It is created here rather than in the XAML because it must not be a child of this window.
            _osd = new OsdOverlayWindow { DataContext = _viewModel };
            _viewModel.OsdChanged += (_, _) => ApplyOsd();
            ApplyOsd();

            _timer.Start();
        }
        catch (Exception ex)
        {
            // An exception out of this handler is raised on the thread pool and kills the process. A window that
            // opened without its telemetry is still a window the user can read the log from.
            AppLog.Error(Loc.T("Pencere açılışı başarısız"), ex.ToString());
        }
    }

    /// <summary>
    /// Raises the banner whenever the view model has something new to say. Only <c>Status</c> matters: every other
    /// property is a reading, and a reading is not an event.
    /// </summary>
    /// <remarks>
    /// A message raised while the window is hidden is not shown. The banner would be invisible anyway, and the only
    /// time the user could see it is the moment the window comes back — which is exactly the stale notification the
    /// Nitro key used to produce.
    /// </remarks>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Status)) return;
        if (_viewModel == null || !_viewModel.HasStatus) return;
        if (!IsVisible) return;

        ShowToast(_viewModel.Status, _viewModel.HasToastAction);
    }

    /// <summary>
    /// Puts the banner on screen for as long as the message needs. The duration is the WPF window's rule, kept
    /// verbatim: three seconds for a short confirmation, longer for a sentence that has to be read, and longer again
    /// when the banner carries a button the user has to notice.
    /// </summary>
    internal static TimeSpan ToastDurationFor(string? message, bool hasAction = false)
    {
        const int floorSeconds = 3;
        const int ceilingSeconds = 10;
        const int charactersPerSecond = 22;
        const int actionSeconds = 5;
        const int actionCeilingSeconds = 15;

        int length = message?.Length ?? 0;
        int seconds = floorSeconds + (length / charactersPerSecond);
        if (hasAction)
            return TimeSpan.FromSeconds(Math.Clamp(seconds + actionSeconds, floorSeconds + actionSeconds, actionCeilingSeconds));

        return TimeSpan.FromSeconds(Math.Clamp(seconds, floorSeconds, ceilingSeconds));
    }

    private void ShowToast(string message, bool hasAction)
    {
        _toastTimer.Stop();
        _toastTimer.Interval = ToastDurationFor(message, hasAction);

        // Only an actionable banner takes the pointer; otherwise it stays click-through so it never blocks the page
        // underneath it.
        ToastBanner.IsHitTestVisible = hasAction;
        ToastBanner.Opacity = 1;
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastBanner.Opacity = 0;
        ToastBanner.IsHitTestVisible = false;
    }

    /// <summary>
    /// Set by the clean-exit path once the fans have been handed back. Until then a close request only hides the
    /// window, which is what keeps the background work alive.
    /// </summary>
    public bool AllowClose { get; set; }

    /// <summary>
    /// Called when the window comes back from the tray. The keyboard lighting is re-applied because the firmware drops
    /// it on some machines after a suspend, and the polling rate goes back to the visible one.
    /// </summary>
    public void RestoreFromTray()
    {
        UpdateTelemetryPollingRate();
        UpdateChartVisibility();

        if (_viewModel != null) _ = ReassertLightingAsync(_viewModel);
    }

    /// <summary>
    /// Copies text and swallows what the clipboard throws. A platform clipboard can refuse for reasons the app cannot
    /// see — another process holding it, a session with no clipboard at all — and a fire-and-forget task that throws
    /// takes the process down with it.
    /// </summary>
    private static async Task CopyAsync(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Panoya kopyalanamadı"), ex.Message);
        }
    }

    /// <summary>
    /// Re-applies the keyboard lighting after a resume, without letting a failure escape as an unobserved task.
    /// </summary>
    private static async Task ReassertLightingAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.ReassertKeyboardLightingAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Klavye aydınlatması yeniden uygulanamadı"), ex.Message);
        }
    }

    /// <summary>
    /// Brings the window to the front from a trigger that is not the user clicking it — the Nitro key, or a second
    /// launch.
    /// </summary>
    /// <remarks>
    /// <see cref="Window.Activate"/> alone is not enough on Windows. The foreground lock lets a background process
    /// change which window is active but not raise it above the one the user is typing in, so the Win32 call is made
    /// as well; the two together are what actually puts the window on screen.
    /// </remarks>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var handle = TryGetPlatformHandle();
            if (handle != null) SetForegroundWindow(handle.Handle);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Pencere öne getirilemedi"), ex.Message);
        }
    }

    /// <summary>
    /// Drags the window by its header. The window has no system chrome, so this is the only way to move it; a
    /// double-click toggles the maximised state, which is what every other title bar does.
    /// </summary>
    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        BeginMoveDrag(e);
    }

    /// <summary>
    /// Drags the bottom-right corner to resize. The window has no system chrome, so there is no frame to grab: the
    /// grip is the frame, and <see cref="Window.BeginResizeDrag"/> hands the drag to the platform so the resize is the
    /// same one a decorated window would do — no layout work of ours, no per-frame callback.
    /// </summary>
    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        BeginResizeDrag(WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    /// <summary>
    /// The grip is only meaningful on a restored window: a maximised one has no corner to drag, and a grip drawn over
    /// the taskbar edge would be a control that does nothing.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            ResizeGrip.IsVisible = WindowState == WindowState.Normal;
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximized();

    /// <summary>
    /// Puts the rail back to its default width. The splitter is four transparent pixels, so a drag that went too far
    /// has no visible handle to drag back; the double click is the way out, and the tooltip is where it is announced.
    /// </summary>
    private void OnRailSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        vm.SidebarWidth = AppSettings.DefaultSidebarWidth;

        // The column is the thing that actually moves, so it is set as well: writing the view model alone would leave
        // the rail where the drag left it.
        if (_railColumn != null) _railColumn.Width = new GridLength(AppSettings.DefaultSidebarWidth);

        e.Handled = true;
    }

    /// <summary>
    /// Restarts the rail's save timer. A drag raises this once per pixel and the view model writes the settings file
    /// on every assignment, so the write waits for the drag to stop.
    /// </summary>
    private void OnRailColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ColumnDefinition.WidthProperty) return;

        _railSaveTimer.Stop();
        _railSaveTimer.Start();
    }

    /// <summary>
    /// Stores the rail's width. Read from the column rather than from the splitter's delta, so a keyboard resize and a
    /// drag are stored the same way.
    /// </summary>
    private void SaveRailWidth()
    {
        _railSaveTimer.Stop();
        if (_viewModel == null || _railColumn == null) return;

        _viewModel.SidebarWidth = _railColumn.Width.IsAbsolute
            ? _railColumn.Width.Value
            : _railColumn.ActualWidth;
    }

    /// <summary>Hides to the tray rather than ending the process, so the fan curve keeps running.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// A row in the rail's live readout. The tag names the page the row belongs to, so the same handler serves every
    /// row and the XAML stays a list of readings rather than a list of commands.
    /// </summary>
    private void OnSideStatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string key }) return;
        if (DataContext is not MainViewModel vm) return;

        vm.SelectTab(key);
    }

    /// <summary>
    /// The signature's name opens the project. Launched through the shell rather than a link control so the window
    /// does not need a browser dependency, and guarded because a machine without a default browser throws.
    /// </summary>
    private void OnSignatureClick(object? sender, RoutedEventArgs e)
    {
        const string url = "https://github.com/canersevinc/NitroLite";

        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
        }
        catch (Exception)
        {
            // A machine with no browser is not an error worth a dialog: the link simply does nothing.
        }
    }

    /// <summary>
    /// A close request hides the window and trims the working set. The trim is the point of hiding rather than
    /// minimising: a window that is not on screen should not be holding a hundred megabytes of layout.
    /// </summary>
    /// <remarks>
    /// Nothing is reported here. The window is gone, so a banner explaining where it went cannot be read; the only
    /// time it would be seen is when the window comes back, which is the notification the Nitro key used to raise.
    /// </remarks>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose) return;

        e.Cancel = true;
        SaveWindowPosition();
        Hide();
        TrimMemory();
        UpdateTelemetryPollingRate();
        UpdateChartVisibility();
    }

    private async Task TickAsync()
    {
        if (_viewModel == null) return;
        await _viewModel.RefreshTelemetryAsync();

        // Re-placed on every tick because the frame's own size changes with the numbers it holds: a GPU name that
        // wraps would otherwise push a bottom corner off the edge.
        ApplyOsd();
    }

    /// <summary>
    /// The timer's own handler. A tick that throws would escape an <c>async void</c> lambda and take the process down
    /// from the thread pool, so the failure is logged and the next tick still runs.
    /// </summary>
    private async Task TickSafelyAsync()
    {
        try
        {
            await TickAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Error(Loc.T("Telemetri döngüsü başarısız"), ex.ToString());
        }
    }

    /// <summary>Shows, hides or moves the overlay to match the view model.</summary>
    private void ApplyOsd()
    {
        if (_viewModel == null || _osd == null) return;

        _osd.Apply(_viewModel);
        UpdateTelemetryPollingRate();
        UpdateChartVisibility();
    }

    /// <summary>
    /// Slows the loop down while the window is hidden or minimised. The curve still runs, so the fans still follow the
    /// temperature; what changes is how often the sensors are read.
    /// </summary>
    private void UpdateTelemetryPollingRate()
    {
        // The overlay wins over the window's own visibility: a user in a game has the window hidden and the OSD on,
        // and that is exactly the case the fast rate exists for.
        var wanted = _viewModel?.OsdEnabled == true
            ? OsdInterval
            : IsWindowOnScreen ? VisibleInterval : HiddenInterval;
        if (_timer.Interval == wanted) return;

        _timer.Interval = wanted;
        AppLog.Info(Loc.T("Telemetri aralığı değişti"), $"{wanted.TotalSeconds:F0} sn");
    }

    /// <summary>
    /// True when the window is actually on screen: shown and not minimised.
    /// </summary>
    /// <remarks>
    /// <see cref="Window.IsVisible"/> alone is not the answer. A minimised window is still visible as far as Avalonia
    /// is concerned — it is shown, it just has no client area — so the loop kept reading the sensors once a second on
    /// a machine whose owner had minimised the window to save battery. The chart is skipped for the same reason: it
    /// walks the whole history and formats nine strings for a window nobody can see.
    /// </remarks>
    private bool IsWindowOnScreen => IsVisible && WindowState != WindowState.Minimized;

    /// <summary>Tells the view model whether the chart is worth recomputing, and rebuilds it when it becomes visible.</summary>
    private void UpdateChartVisibility()
    {
        if (_viewModel == null) return;
        _viewModel.ChartVisible = IsWindowOnScreen;
    }

    /// <summary>Remembers the size and position the window has while it is not maximised.</summary>
    private void CaptureNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;

        _normalPosition = Position;
        _normalSize = ClientSize;
        _hasNormalBounds = true;
    }

    /// <summary>Puts the window back where it was, if the stored position is still on a screen.</summary>
    private void RestoreWindowPosition()
    {
        if (_viewModel == null) return;

        var (left, top, width, height, maximized) = _viewModel.ReadWindowPlacement();
        if (width is double w && height is double h && w >= MinWidth && h >= MinHeight)
        {
            Width = w;
            Height = h;
        }

        if (left is double x && top is double y && IsOnScreen(x, y))
        {
            Position = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        }

        if (maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// True when the point is inside one of the screens. A window restored onto a monitor that has since been
    /// unplugged would otherwise open off-screen with no way to reach it.
    /// </summary>
    private bool IsOnScreen(double x, double y)
    {
        var screens = Screens;
        if (screens == null) return false;

        foreach (var screen in screens.All)
        {
            var area = screen.WorkingArea;
            if (x >= area.X - 40 && x <= area.X + area.Width - 40 &&
                y >= area.Y - 40 && y <= area.Y + area.Height - 40)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes the current placement to the settings file.</summary>
    private void SaveWindowPosition()
    {
        if (_viewModel == null) return;

        var maximized = WindowState == WindowState.Maximized;

        // The restored size, not the maximised one: a window saved at 1920x1080 would come back maximised on a smaller
        // screen and refuse to shrink.
        var width = _hasNormalBounds ? _normalSize.Width : Width;
        var height = _hasNormalBounds ? _normalSize.Height : Height;
        var left = _hasNormalBounds ? _normalPosition.X : Position.X;
        var top = _hasNormalBounds ? _normalPosition.Y : Position.Y;

        _viewModel.SaveWindowPlacement(left, top, width, height, maximized);
    }

    /// <summary>
    /// Hands the working set back to the operating system. A hidden window that keeps its layout in memory is the
    /// difference between a tray app that costs 40 MB and one that costs 200.
    /// </summary>
    private static void TrimMemory()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, -1, -1);
        }
        catch (Exception ex)
        {
            AppLog.Warn(Loc.T("Bellek kırpılamadı"), ex.Message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
