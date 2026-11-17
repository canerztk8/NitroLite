using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace NitroLite.Desktop.Controls;

/// <summary>
/// A slider whose whole track is the handle, not just the eight-pixel thumb.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia's <see cref="Slider"/> starts a drag only from a press on the two RepeatButtons inside its
/// <see cref="Track"/>. In this theme those buttons are arranged with no size at all, so a click anywhere on the track
/// did nothing and the only way to change a value was to hit the thumb exactly — which reads as a slider that cannot be
/// dragged. The press is handled here instead, from the pointer's position along the track, which is the same rule the
/// thumb's own drag uses.
/// </para>
/// <para>
/// The thumb is left alone: a press that lands on it is passed through, so the built-in drag keeps working and the two
/// paths never fight over the value.
/// </para>
/// </remarks>
public class NitroSlider : Slider
{
    private bool _dragging;

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsInsideThumb(e.Source as Visual)) return;

        _dragging = true;
        e.Pointer.Capture(this);
        MoveTo(e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging) return;

        // A release outside the window never reaches OnPointerReleased, so the button is checked as well: without
        // this the value would keep following the pointer after the drag had ended.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = false;
            return;
        }

        MoveTo(e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>True when the press started on the thumb, which drags itself.</summary>
    private bool IsInsideThumb(Visual? source)
    {
        for (var visual = source; visual != null && visual != this; visual = visual.GetVisualParent())
        {
            if (visual is Thumb) return true;
        }

        return false;
    }

    /// <summary>Sets the value from a point in the slider's own coordinates.</summary>
    private void MoveTo(Point point)
    {
        var track = this.GetVisualDescendants().OfType<Track>().FirstOrDefault();
        if (track == null) return;

        var origin = track.TranslatePoint(default, this) ?? default;
        var thumbLength = track.Thumb?.Bounds.Width ?? 0;
        var usable = track.Bounds.Width - thumbLength;
        if (usable <= 0) return;

        var ratio = Math.Clamp((point.X - origin.X - (thumbLength / 2)) / usable, 0, 1);
        if (IsDirectionReversed) ratio = 1 - ratio;

        var value = Minimum + (ratio * (Maximum - Minimum));
        if (IsSnapToTickEnabled && TickFrequency > 0)
        {
            value = Minimum + (Math.Round((value - Minimum) / TickFrequency) * TickFrequency);
        }

        SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }
}
