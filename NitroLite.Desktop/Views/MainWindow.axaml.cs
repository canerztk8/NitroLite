using System;
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

    private readonly DispatcherTimer _timer;
    private MainViewModel? _viewModel;

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
        _timer.Tick += async (_, _) => await TickAsync();

        Opened += async (_, _) =>
        {
            // The clipboard is the only platform service the view model needs, and it is a delegate so a test can
            // leave it unset. Set it before the first command can run.
            ClipboardService.Setter = text =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null) _ = clipboard.SetTextAsync(text);
            };

            _viewModel = DataContext as MainViewModel;
            if (_viewModel == null) return;

            RestoreWindowPosition();
            await _viewModel.InitializeAsync();
            _timer.Start();
        };

        PositionChanged += (_, _) => CaptureNormalBounds();
        SizeChanged += (_, _) => CaptureNormalBounds();

        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
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

        if (_viewModel != null) _ = _viewModel.ReassertKeyboardLightingAsync();
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

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximized();

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
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose) return;

        e.Cancel = true;
        SaveWindowPosition();
        Hide();
        TrimMemory();
        UpdateTelemetryPollingRate();
        _viewModel?.NotifyHiddenToTray();
    }

    private async Task TickAsync()
    {
        if (_viewModel == null) return;
        await _viewModel.RefreshTelemetryAsync();
    }

    /// <summary>
    /// Slows the loop down while the window is hidden. The curve still runs, so the fans still follow the temperature;
    /// what changes is how often the sensors are read.
    /// </summary>
    private void UpdateTelemetryPollingRate()
    {
        var wanted = IsVisible ? VisibleInterval : HiddenInterval;
        if (_timer.Interval == wanted) return;

        _timer.Interval = wanted;
        AppLog.Info("Telemetri aralığı değişti", $"{wanted.TotalSeconds:F0} sn");
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
            AppLog.Warn("Bellek kırpılamadı", ex.Message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);
}
