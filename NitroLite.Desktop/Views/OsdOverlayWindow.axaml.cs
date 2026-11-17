using System;
using Avalonia;
using Avalonia.Controls;
using NitroLite.Core;
using NitroLite.Desktop.ViewModels;

namespace NitroLite.Desktop.Views;

/// <summary>
/// The in-game overlay: three lines of telemetry in a small frame that floats over whatever is running.
/// </summary>
/// <remarks>
/// <para>
/// Every property that makes an overlay an overlay rather than a window is set in the XAML: no decorations, no taskbar
/// entry, not resizable, not focusable, not activated on show, always on top, transparent. The one thing that cannot
/// be expressed there is where it goes, which is what this file does.
/// </para>
/// <para>
/// It binds to the main view model rather than carrying one of its own. The OSD and the monitoring page then show the
/// same numbers on the same tick by construction, and there is no second timer to drift.
/// </para>
/// </remarks>
public partial class OsdOverlayWindow : Window
{
    /// <summary>How far the frame sits from the screen edge, in device-independent pixels.</summary>
    private const double EdgeMargin = 18;

    public OsdOverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Puts the frame in the corner the view model asked for, on the display it asked for, and shows or hides it.
    /// </summary>
    /// <remarks>
    /// Called on every telemetry tick as well as on a settings change, because the frame's own size changes with the
    /// numbers it holds: a GPU name that wraps to two lines would otherwise push the bottom corners off the edge.
    /// </remarks>
    public void Apply(MainViewModel viewModel)
    {
        if (!viewModel.OsdEnabled)
        {
            if (IsVisible) Hide();
            return;
        }

        var screens = Screens;
        if (screens == null) return;

        var all = screens.All;
        if (all.Count == 0) return;

        // The window is the only thing that can count displays, so it tells the view model rather than the view model
        // guessing and cycling onto a screen that is not there.
        viewModel.OsdMonitorCount = all.Count;
        var screen = all[Math.Clamp(viewModel.OsdMonitor, 0, all.Count - 1)];
        var area = screen.WorkingArea;
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

        // The frame's own size in device-independent pixels. The XAML sizes the frame to its content, so there is no
        // declared width to fall back on: before the first layout the content is measured here instead, because the
        // frame has to be placed before it is shown and placing it needs its size.
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            Measure(Size.Infinity);
            width = DesiredSize.Width;
            height = DesiredSize.Height;
        }

        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return;

        double margin = EdgeMargin * scale;
        double left = viewModel.OsdCorner is 1 or 3
            ? area.X + area.Width - width - margin
            : area.X + margin;
        double top = viewModel.OsdCorner is 2 or 3
            ? area.Y + area.Height - height - margin
            : area.Y + margin;

        // Placed before it is shown, never after. Moving a window that is already on screen makes the Windows backend
        // rebuild its composition target, and the overlay then stays invisible: the window is there, topmost and the
        // right size, and nothing is drawn into it. That is the bug this ordering exists for.
        //
        // Assigned only when the corner actually changed. The overlay is re-placed on every telemetry tick, and a
        // write of the same coordinates is still a move as far as the window manager is concerned: it rebuilt the
        // composition target once a second for a window that had not gone anywhere.
        var target = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
        if (Position != target) Position = target;

        if (!IsVisible) Show();
    }

    /// <summary>
    /// A click on the overlay moves it to the next corner. The frame is not focusable, so this is the only interaction
    /// it offers; it is also the fastest way to get it out of the way of a health bar.
    /// </summary>
    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (DataContext is MainViewModel vm) vm.CycleOsdCornerCommand.Execute(null);
    }
}
