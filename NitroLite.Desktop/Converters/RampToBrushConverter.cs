using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NitroLite.Core;

namespace NitroLite.Desktop.Converters;

/// <summary>
/// Paints a temperature ramp as a gradient, so a profile chip shows the colours it will put on the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// The three temp-reactive profiles differ only in their stops, and a name alone — "Inferno (Kehribar → Kırmızı)" — is
/// a description, not a preview: the user has to select the profile and read the keyboard to find out what it does. The
/// chip draws the same ramp the evaluator walks, from the coldest stop on the left to the hottest on the right, which
/// makes the choice a look rather than a guess.
/// </para>
/// <para>
/// The stops are sanitised rather than trusted, exactly as the evaluator does it: a ramp read from a settings file can
/// carry a colour that is not a colour or a temperature out of order, and a gradient is not the place to throw. An
/// unusable entry is dropped; a ramp with one usable stop paints that colour flat; an empty one paints nothing.
/// </para>
/// <para>
/// A brush is built per call rather than cached: the custom chip's ramp is replaced on every edit, so a cache would
/// have to be keyed on the whole stop list and would hold one entry per degree the user dragged through.
/// </para>
/// </remarks>
public sealed class RampToBrushConverter : IValueConverter
{
    /// <summary>The shared instance the views bind to.</summary>
    public static readonly RampToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<ReactiveLightingStop> stops) return Brushes.Transparent;

        var clean = ReactiveLightingProfiles.SanitizeStops(stops);
        if (clean.Count == 0) return Brushes.Transparent;

        if (clean.Count == 1) return new SolidColorBrush(Parse(clean[0].Color));

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };

        // The coldest stop sits at the left edge and the hottest at the right one, whatever temperatures the ramp
        // actually uses: two ramps that cover 45–90 and 30–100 still read as the same left-to-right story.
        var min = clean[0].AtCelsius;
        var max = clean[^1].AtCelsius;
        var span = max - min;

        foreach (var stop in clean)
        {
            double offset = span <= 0 ? 0 : Math.Clamp((stop.AtCelsius - min) / span, 0, 1);
            brush.GradientStops.Add(new GradientStop(Parse(stop.Color), offset));
        }

        return brush;
    }

    /// <summary>
    /// A colour that survived sanitising, or transparent. <c>#RRGGBB</c> is the only shape that reaches this method,
    /// and transparent keeps a bad entry from painting a black band in the middle of an otherwise valid ramp.
    /// </summary>
    private static Color Parse(string hex) =>
        Color.TryParse(hex, out var color) ? color : Colors.Transparent;

    /// <summary>A gradient is a picture of a ramp, not one of its stops, so there is nothing to convert back to.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A gradient is a picture of a ramp, not a colour the backend can take.");
}
