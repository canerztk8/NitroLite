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

        Place(area, scale, new Size(width, height), viewModel.OsdCorner);

        if (!IsVisible) Show();
    }

    /// <summary>
    /// The frame's size is only known after layout, and Apply runs in the same tick that changed the text, before
    /// that layout: the size it read was the previous tick's. A line that grew made the window grow to the right of a
    /// position computed for the narrower frame, and the right-hand corners stayed clipped until the next tick. The
    /// platform reports the real size once the layout has happened, and the frame is placed again from that.
    /// </summary>
    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);

        if (!IsVisible || DataContext is not MainViewModel viewModel || !viewModel.OsdEnabled) return;

        // The left and top corners do not depend on the size, so for them this ends at the unchanged-position check
        // below and nothing moves.
        var all = Screens?.All;
        if (all == null || all.Count == 0) return;

        var screen = all[Math.Clamp(viewModel.OsdMonitor, 0, all.Count - 1)];
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var size = e.ClientSize;
        if (double.IsNaN(size.Width) || double.IsNaN(size.Height) || size.Width <= 0 || size.Height <= 0) return;

        Place(screen.WorkingArea, scale, size, viewModel.OsdCorner);
    }

    private void Place(PixelRect area, double scale, Size frame, int corner)
    {
        // Placed before it is shown, never after. Moving a window that is already on screen makes the Windows backend
        // rebuild its composition target, and the overlay then stays invisible: the window is there, topmost and the
        // right size, and nothing is drawn into it. That is the bug this ordering exists for.
        //
        // Assigned only when the corner actually changed. The overlay is re-placed on every telemetry tick, and a
        // write of the same coordinates is still a move as far as the window manager is concerned: it rebuilt the
        // composition target once a second for a window that had not gone anywhere. The same check is what ends the
        // size-changed, move, size-changed chain: a move does not change the size, so the second pass finds the
        // position already right and writes nothing.
        var target = ComputePosition(area, scale, frame, corner, EdgeMargin);
        if (Position != target) Position = target;
    }

    /// <summary>
    /// Where the frame's top-left corner goes, in physical pixels: the requested corner of the working area, inset by
    /// the margin, and never outside it.
    /// </summary>
    /// <param name="workingArea">The display's working area in physical pixels (the taskbar is already excluded).</param>
    /// <param name="scaling">The display's scale factor; converts the frame and the margin from DIPs to pixels.</param>
    /// <param name="frame">The frame's size in device-independent pixels.</param>
    /// <param name="corner">0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right.</param>
    /// <param name="margin">The gap to the edge in device-independent pixels.</param>
    /// <remarks>
    /// The clamp is the last word: a frame that has grown past its margin, or is wider than the display, starts at the
    /// working area's left or top edge rather than hanging off it, so the part that is cut off is the tail of a line
    /// and not its start. The right and bottom edges win over the margin, the left and top edges win over both.
    /// </remarks>
    public static PixelPoint ComputePosition(PixelRect workingArea, double scaling, Size frame, int corner, double margin)
    {
        var scale = scaling <= 0 ? 1.0 : scaling;
        double widthPx = Math.Ceiling(frame.Width * scale);
        double heightPx = Math.Ceiling(frame.Height * scale);
        double marginPx = margin * scale;

        double left = corner is 1 or 3
            ? workingArea.Right - widthPx - marginPx
            : workingArea.X + marginPx;
        double top = corner is 2 or 3
            ? workingArea.Bottom - heightPx - marginPx
            : workingArea.Y + marginPx;

        left = Math.Max(workingArea.X, Math.Min(left, workingArea.Right - widthPx));
        top = Math.Max(workingArea.Y, Math.Min(top, workingArea.Bottom - heightPx));

        return new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
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
