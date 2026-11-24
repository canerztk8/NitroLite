using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NitroLite.Core;

namespace NitroLite.Desktop.Views;

/// <summary>
/// The colour picker the palette's last swatch opens.
/// </summary>
/// <remarks>
/// A window of its own rather than a flyout: the square, the hue bar and the hex box together are taller than the card
/// they would hang off, and a flyout that covers the keyboard preview is the wrong trade on a page whose whole point is
/// watching the colour land.
///
/// The colour is held as hue/saturation/value rather than as an <see cref="Color"/>, because that is the space the two
/// drag surfaces move in. A grey has no hue of its own, so the hue is kept when saturation reaches zero instead of
/// being recomputed from the grey — otherwise dragging to the left edge of the square would snap the hue back to red
/// and the square would jump under the pointer.
/// </remarks>
public partial class ColorPickerDialog : Window
{
    /// <summary>Hue in degrees, 0 to 360.</summary>
    private double _hue;

    /// <summary>Saturation, 0 to 1.</summary>
    private double _saturation;

    /// <summary>Value, 0 to 1.</summary>
    private double _value;

    /// <summary>Guards the hex box against echoing its own update back into the colour.</summary>
    private bool _syncingHex;

    private bool _draggingSquare;
    private bool _draggingHue;

    public ColorPickerDialog()
    {
        InitializeComponent();

        HexBox.TextChanged += OnHexChanged;
    }

    /// <param name="startHex">The colour the active target already holds, so the dialog opens on it.</param>
    /// <param name="title">Already translated: this window has no view model and owns no language.</param>
    public ColorPickerDialog(string startHex, string title) : this()
    {
        TitleText.Text = title;
        CancelButton.Content = Loc.T("Vazgeç");
        ApplyButton.Content = Loc.T("Uygula");
        SetColor(Parse(startHex));
    }

    /// <summary>The colour the user settled on, as <c>#RRGGBB</c>. Only meaningful after a true result.</summary>
    public string PickedHex { get; private set; } = "#FFFFFF";

    /// <summary>
    /// The markers are placed from the laid-out size, which is zero until the window has been measured. Redrawing on
    /// open is what puts them where the colour actually is rather than in the top-left corner.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Refresh();
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        PickedHex = Format(CurrentColor());
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    // ---------------------------------------------------------------- the saturation/value square

    private void OnSvPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SvArea).Properties.IsLeftButtonPressed) return;

        e.Pointer.Capture(SvArea);
        _draggingSquare = true;
        DragSquare(e.GetPosition(SvArea));
    }

    private void OnSvPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingSquare) DragSquare(e.GetPosition(SvArea));
    }

    private void OnSvPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingSquare = false;
        e.Pointer.Capture(null);
    }

    private void DragSquare(Point point)
    {
        var width = SvArea.Bounds.Width;
        var height = SvArea.Bounds.Height;
        if (width <= 0 || height <= 0) return;

        _saturation = Math.Clamp(point.X / width, 0, 1);
        _value = 1 - Math.Clamp(point.Y / height, 0, 1);
        Refresh();
    }

    // ---------------------------------------------------------------- the hue bar

    private void OnHuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(HueTrack).Properties.IsLeftButtonPressed) return;

        e.Pointer.Capture(HueTrack);
        _draggingHue = true;
        DragHue(e.GetPosition(HueTrack));
    }

    private void OnHuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingHue) DragHue(e.GetPosition(HueTrack));
    }

    private void OnHuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingHue = false;
        e.Pointer.Capture(null);
    }

    private void DragHue(Point point)
    {
        var width = HueTrack.Bounds.Width;
        if (width <= 0) return;

        _hue = Math.Clamp(point.X / width, 0, 1) * 360;
        Refresh();
    }

    // ---------------------------------------------------------------- the hex box

    /// <summary>
    /// Types a hex into the box. A half-typed value is left alone rather than snapped to a default: the box is the one
    /// place a colour can be entered exactly, and rewriting it mid-keystroke would make it unusable.
    /// </summary>
    /// <remarks>
    /// Only a complete <c>#RRGGBB</c> (or the six digits alone) is taken. <c>Color.TryParse</c>
    /// also accepts the short <c>#RGB</c> form, so "#00E" on the way to "#00E5FF" parsed as #0000EE and the box was
    /// rewritten under the user's cursor. And the box is not written back from its own edit: the colour round-trips
    /// through hue/saturation/value, and a rounding step there would otherwise replace what was typed.
    /// </remarks>
    private void OnHexChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingHex) return;
        if (!TryParseFullHex(HexBox.Text, out var color)) return;

        _typingHex = true;
        try { SetColor(color); }
        finally { _typingHex = false; }
    }

    /// <summary>True while the colour is being adopted from the hex box, so the box is not rewritten from itself.</summary>
    private bool _typingHex;

    /// <summary>Parses exactly six hex digits, with or without a leading '#'. Anything shorter is still being typed.</summary>
    private static bool TryParseFullHex(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim() ?? "";
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit)) return false;

        return Color.TryParse("#" + hex, out color);
    }

    // ---------------------------------------------------------------- painting

    /// <summary>Adopts a colour and repaints everything that shows it.</summary>
    private void SetColor(Color color)
    {
        var (hue, saturation, value) = ToHsv(color);

        // A grey carries no hue, so the one already chosen is kept rather than reset to red.
        if (saturation > 0) _hue = hue;
        _saturation = saturation;
        _value = value;

        Refresh();
    }

    private void Refresh()
    {
        SvHueLayer.Fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(FromHsv(_hue, 1, 1), 1)
            }
        };

        var squareWidth = SvArea.Bounds.Width;
        var squareHeight = SvArea.Bounds.Height;
        var markerX = _saturation * squareWidth;
        var markerY = (1 - _value) * squareHeight;
        PlaceMarker(SvMarker, markerX, markerY, squareWidth, squareHeight);
        PlaceMarker(SvMarkerRing, markerX, markerY, squareWidth, squareHeight);

        var hueWidth = HueTrack.Bounds.Width;
        var hueLeft = _hue / 360 * hueWidth - HueMarker.Width / 2;
        Canvas.SetLeft(HueMarker, Math.Clamp(hueLeft, 0, Math.Max(0, hueWidth - HueMarker.Width)));

        var color = CurrentColor();
        PreviewSwatch.Background = new SolidColorBrush(color);
        WriteHexBox(color);
    }

    /// <summary>
    /// Centres a marker on a point, held inside the square. At full value or full saturation the point is on the edge,
    /// and a marker centred there would be half clipped by the square's own clip — so it is nudged in by its radius
    /// instead, which is what every picker does at the extremes.
    /// </summary>
    private static void PlaceMarker(Control marker, double centerX, double centerY, double width, double height)
    {
        var halfWidth = marker.Width / 2;
        var halfHeight = marker.Height / 2;

        Canvas.SetLeft(marker, Math.Clamp(centerX - halfWidth, 0, Math.Max(0, width - marker.Width)));
        Canvas.SetTop(marker, Math.Clamp(centerY - halfHeight, 0, Math.Max(0, height - marker.Height)));
    }
    private Color CurrentColor() => FromHsv(_hue, _saturation, _value);

    private void WriteHexBox(Color color)
    {
        // The user's own text stays as typed; see OnHexChanged.
        if (_typingHex) return;

        var hex = Format(color);
        if (HexBox.Text == hex) return;

        _syncingHex = true;
        try { HexBox.Text = hex; }
        finally { _syncingHex = false; }
    }

    // ---------------------------------------------------------------- colour maths

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;

        var chroma = value * saturation;
        var second = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var floor = value - chroma;

        var (red, green, blue) = hue switch
        {
            < 60 => (chroma, second, 0d),
            < 120 => (second, chroma, 0d),
            < 180 => (0d, chroma, second),
            < 240 => (0d, second, chroma),
            < 300 => (second, 0d, chroma),
            _ => (chroma, 0d, second)
        };

        return Color.FromRgb(Channel(red + floor), Channel(green + floor), Channel(blue + floor));
    }

    private static byte Channel(double component) =>
        (byte)Math.Clamp(Math.Round(component * 255), 0, 255);

    private static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double red = color.R / 255d, green = color.G / 255d, blue = color.B / 255d;

        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;

        var hue = 0d;
        if (delta > 0)
        {
            if (max == red) hue = 60 * (((green - blue) / delta) % 6);
            else if (max == green) hue = 60 * ((blue - red) / delta + 2);
            else hue = 60 * ((red - green) / delta + 4);
        }

        if (hue < 0) hue += 360;

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>A stored colour that will not parse opens the dialog on white rather than on nothing.</summary>
    private static Color Parse(string hex) => Color.TryParse(hex, out var color) ? color : Colors.White;
}
