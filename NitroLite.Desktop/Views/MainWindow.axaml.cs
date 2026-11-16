using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;

namespace NitroLite.Desktop.Views;

/// <summary>
/// The shell window: header, three tabs, status line, and the telemetry timer.
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
/// </remarks>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
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

            await _viewModel.InitializeAsync();
            _timer.Start();
        };

        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Set by the clean-exit path once the fans have been handed back. Until then a close request only hides the
    /// window, which is what keeps the background work alive.
    /// </summary>
    public bool AllowClose { get; set; }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (AllowClose) return;

        e.Cancel = true;
        Hide();
        _viewModel?.NotifyHiddenToTray();
    }

    private async Task TickAsync()
    {
        if (_viewModel == null) return;

        try
        {
            await _viewModel.RefreshTelemetryAsync();
        }
        catch (Exception ex)
        {
            // A telemetry read that throws must not take the window down: the next tick will try again.
            AppLog.Warn("Telemetri döngüsü hatası", ex.Message);
        }
    }
}
