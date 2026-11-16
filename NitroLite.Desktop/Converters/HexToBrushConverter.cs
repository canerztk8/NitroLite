using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NitroLite.Desktop.Converters;

/// <summary>
/// Paints a <c>#RRGGBB</c> string as a brush, so a colour the backend takes as text can also be shown as a colour.
/// </summary>
/// <remarks>
/// The view model keeps colours as the hex strings the sysfs and WMI protocols accept, because that is what gets
/// written and what the user can edit. A swatch or a zone preview needs a brush, and this is the one place that
/// conversion happens. An unparseable value paints nothing rather than throwing: a half-typed hex in the text box
/// should leave the preview blank, not take the window down.
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    /// <summary>The shared instance the views bind to.</summary>
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return Brushes.Transparent;

        var hex = text.Trim();
        if (hex.Length == 0) return Brushes.Transparent;
        if (hex[0] != '#') hex = "#" + hex;

        return Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A brush is not a colour the backend can take.");
}
