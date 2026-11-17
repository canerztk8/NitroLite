using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;

namespace NitroLite.Desktop;

/// <summary>
/// The system tray icon: the reason closing the window does not stop the fan curve.
/// </summary>
/// <remarks>
/// <para>
/// The window hides instead of closing, so the telemetry timer keeps running and a custom fan curve keeps following
/// the CPU temperature while the user is in a game. That is the whole point of the tray: a fan curve that stops when
/// the window is closed is a fan curve that stops exactly when the machine gets hot.
/// </para>
/// <para>
/// The menu is built from <see cref="MainViewModel.QuickModes"/> rather than from a second hard-coded list, so the
/// tray and the mode card can never disagree about which modes exist. Each item calls the same
/// <see cref="MainViewModel.ApplyModeFromTrayAsync"/> the card uses, which means the battery rule for Turbo and
/// Performance applies here too.
/// </para>
/// <para>
/// A mode the firmware does not advertise is hidden rather than disabled, and a mode the battery rule forbids is
/// disabled with the reason in its tooltip. Offering a click that can only be refused is worse than not offering it,
/// but hiding a mode the machine is currently in would leave the menu with no marked row.
/// </para>
/// <para>
/// "Temiz Çıkış" is deliberately not "Çıkış": it hands the fans back to the firmware before the process ends. A
/// machine left with a fixed fan speed written by a program that is no longer running is the one failure mode a tray
/// menu can cause, and it is worth a slower exit to avoid it.
/// </para>
/// </remarks>
public sealed class TrayIconHost : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly NativeMenu _menu;
    private readonly Dictionary<OperatingMode, NativeMenuItem> _modeItems = new();
    private readonly Dictionary<CpuBoostMode, NativeMenuItem> _boostItems = new();
    private readonly NativeMenuItem _showItem;
    private readonly NativeMenuItem _retryItem;
    private readonly NativeMenuItem _boostItem;
    private readonly NativeMenuItem _exitItem;
    private bool _disposed;

    /// <summary>
    /// When the icon was last pressed. Avalonia's tray icon reports one <c>Clicked</c> per press and has no
    /// double-click event, so the second press inside <see cref="TrayMenuPolicy.DoubleClickWindow"/> is what counts as
    /// one. Both paths show the window; the distinction is what lets the host log the gesture the user made.
    /// </summary>
    private DateTime _lastClickUtc = DateTime.MinValue;

    /// <param name="showWindow">Brings the window back. Called by the icon and by "Pencereyi Göster".</param>
    /// <param name="exit">Runs the clean exit and shuts the application down.</param>
    public TrayIconHost(MainViewModel viewModel, Action showWindow, Action exit)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(exit);

        _menu = new NativeMenu();

        _showItem = new NativeMenuItem(_viewModel.Labels.TrayShow);
        _showItem.Click += (_, _) => showWindow();
        _menu.Add(_showItem);

        // Only meaningful while a desktop restore is pending: the app wrote a mode the firmware refused, and the user
        // can ask it to try again without opening the window.
        _retryItem = new NativeMenuItem(_viewModel.Labels.TrayRetryDesktop)
        {
            IsEnabled = false,
            IsVisible = false
        };
        _retryItem.Click += async (_, _) => await _viewModel.RetryDesktopRestoreAsync().ConfigureAwait(true);
        _menu.Add(_retryItem);

        _menu.Add(new NativeMenuItemSeparator());

        // The five modes are top-level rows, as the WPF tray listed them. A "Hızlı mod" submenu cost a hover and a
        // second click for the one thing the tray exists for, and hid the tick until it opened.
        foreach (var mode in _viewModel.QuickModes)
        {
            var item = new NativeMenuItem(_viewModel.QuickModeLabel(mode))
            {
                ToggleType = NativeMenuItemToggleType.CheckBox
            };
            item.Click += async (_, _) => await _viewModel.ApplyModeFromTrayAsync(mode).ConfigureAwait(true);

            _menu.Add(item);
            _modeItems[mode] = item;
        }

        _menu.Add(new NativeMenuItemSeparator());

        _boostItem = new NativeMenuItem(_viewModel.Labels.TrayCpuBoost) { Menu = new NativeMenu() };
        foreach (var mode in TrayMenuPolicy.BoostModes)
        {
            var item = new NativeMenuItem(ModePolicy.BoostDisplayName(mode))
            {
                ToggleType = NativeMenuItemToggleType.Radio
            };
            item.Click += async (_, _) => await _viewModel.ApplyCpuBoostFromTrayAsync(mode).ConfigureAwait(true);

            _boostItem.Menu!.Add(item);
            _boostItems[mode] = item;
        }
        _menu.Add(_boostItem);

        _menu.Add(new NativeMenuItemSeparator());

        _exitItem = new NativeMenuItem(_viewModel.Labels.TrayCleanExit);
        _exitItem.Click += (_, _) => exit();
        _menu.Add(_exitItem);

        // The tick is set when the menu opens rather than when a mode is applied: the mode can also change from the
        // window, the keyboard key or a game profile, and a stale tick would point at the wrong row.
        _menu.Opening += (_, _) => SyncCheckedState();

        Icon = new TrayIcon
        {
            ToolTipText = _viewModel.Labels.AppTitle,
            Menu = _menu,
            IsVisible = true,
            Icon = LoadIcon()
        };
        Icon.Clicked += (_, _) => OnIconClicked(showWindow);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>The icon itself. The application owns it and adds it to <c>Application.TrayIcons</c>.</summary>
    public TrayIcon Icon { get; }

    /// <summary>The menu, exposed so a test can walk it without a display.</summary>
    public NativeMenu Menu => _menu;

    /// <summary>The quick-mode rows, in the order they appear.</summary>
    public IReadOnlyDictionary<OperatingMode, NativeMenuItem> ModeItems => _modeItems;

    /// <summary>The CPU-boost rows, in the order they appear.</summary>
    public IReadOnlyDictionary<CpuBoostMode, NativeMenuItem> BoostItems => _boostItems;

    /// <summary>The "try the desktop settings again" row.</summary>
    public NativeMenuItem RetryItem => _retryItem;

    /// <summary>
    /// Marks the mode the machine is actually in, hides the modes the firmware does not advertise, and disables the
    /// ones the battery rule forbids. Called when the menu opens.
    /// </summary>
    public void SyncCheckedState()
    {
        var active = _viewModel.SelectedMode?.Mode;

        foreach (var (mode, item) in _modeItems)
        {
            item.IsChecked = TrayMenuPolicy.IsModeChecked(mode, active);

            // A mode the machine is in stays visible even when its group is hidden: a menu with no marked row would
            // leave the user guessing which mode the machine is running.
            item.IsVisible = TrayMenuPolicy.ShowMode(_viewModel.IsModeSupported(mode), mode, active);

            var allowed = _viewModel.IsModeAllowed(mode);
            item.IsEnabled = TrayMenuPolicy.EnableMode(allowed);
            item.ToolTip = TrayMenuPolicy.ModeToolTip(allowed);
        }

        var boostAvailable = _viewModel.CpuBoostAvailable;
        _boostItem.IsVisible = TrayMenuPolicy.ShowBoost(boostAvailable);
        _boostItem.IsEnabled = TrayMenuPolicy.ShowBoost(boostAvailable);

        var currentBoost = _viewModel.SelectedCpuBoost?.Mode;
        foreach (var (mode, item) in _boostItems) item.IsChecked = mode == currentBoost;

        var pending = _viewModel.HasPendingDesktopRestore;
        _retryItem.IsVisible = TrayMenuPolicy.ShowRetry(pending);
        _retryItem.IsEnabled = TrayMenuPolicy.EnableRetry(pending, _viewModel.GameActive);
    }

    /// <summary>
    /// One press of the icon. The window comes back either way; the interval is what tells a double click from two
    /// single ones, and the log line is the only place the difference is visible.
    /// </summary>
    private void OnIconClicked(Action showWindow)
    {
        var now = DateTime.UtcNow;
        var sinceLast = now - _lastClickUtc;
        _lastClickUtc = now;

        if (TrayMenuPolicy.IsDoubleClick(sinceLast))
        {
            AppLog.Info("Tepsi simgesine çift tıklandı", "pencere öne getiriliyor");
        }

        showWindow();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The whole label object is replaced when the language changes, so every header is rewritten at once.
        if (e.PropertyName != nameof(MainViewModel.Labels)) return;
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        var labels = _viewModel.Labels;
        _showItem.Header = labels.TrayShow;
        _retryItem.Header = labels.TrayRetryDesktop;
        _boostItem.Header = labels.TrayCpuBoost;
        _exitItem.Header = labels.TrayCleanExit;
        Icon.ToolTipText = labels.AppTitle;

        foreach (var (mode, item) in _modeItems) item.Header = _viewModel.QuickModeLabel(mode);
        foreach (var (mode, item) in _boostItems) item.Header = ModePolicy.BoostDisplayName(mode);
    }

    /// <summary>
    /// The window icon, embedded in this assembly. A tray icon with no image is invisible on most desktops, so a
    /// missing resource is the one failure worth logging rather than swallowing.
    /// </summary>
    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://NitroLite.Desktop/Assets/app.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Tepsi simgesi yüklenemedi", ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Icon.IsVisible = false;
        Icon.Dispose();
    }
}
