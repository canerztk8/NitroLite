using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NitroLite.Desktop.Converters;

/// <summary>
/// Paints a <c>#RRGGBB</c> string as a brush, so a colour the backend takes as text can also be shown as a colour.
/// </summary>
/// <remarks>
/// <para>
/// The view model keeps colours as the hex strings the sysfs and WMI protocols accept, because that is what gets
/// written and what the user can edit. A swatch or a zone preview needs a brush, and this is the one place that
/// conversion happens. An unparseable value paints nothing rather than throwing: a half-typed hex in the text box
/// should leave the preview blank, not take the window down.
/// </para>
/// <para>
/// Brushes are cached. A binding re-evaluates whenever the value changes, and the lighting page binds four zone
/// previews, the palette and the active colour through this converter — the same handful of hex strings, converted
/// again on every pass. The cache is bounded because the colour picker's text box produces a new string per keystroke,
/// and an unbounded dictionary would keep every one of them for the life of the process.
/// </para>
/// <para>
/// The cached brushes are shared, which is safe here because every use is a <c>Background</c> or <c>Fill</c> binding
/// and nothing mutates the brush it is handed. <see cref="Brushes.Transparent"/> is already a shared instance, so the
/// convention is not new.
/// </para>
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    /// <summary>The shared instance the views bind to.</summary>
    public static readonly HexToBrushConverter Instance = new();

    /// <summary>
    /// How many distinct colours are remembered. A palette is a dozen entries and the picker's history is a few more,
    /// so this is far above what a session uses and far below anything that would hold a leak.
    /// </summary>
    private const int MaxEntries = 256;

    private readonly ConcurrentDictionary<string, IBrush> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many colours the cache is holding. Public rather than internal because the test project has no
    /// <c>InternalsVisibleTo</c> and the bound is the one thing about this class that a test has to be able to see.
    /// </summary>
    public int CachedCount => _cache.Count;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return Brushes.Transparent;

        var hex = text.Trim();
        if (hex.Length == 0) return Brushes.Transparent;
        if (hex[0] != '#') hex = "#" + hex;

        if (_cache.TryGetValue(hex, out var cached)) return cached;

        IBrush brush = Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;

        // A failed parse is not cached: the text box is mid-edit, and the next keystroke is a different string anyway.
        if (brush != Brushes.Transparent && _cache.Count < MaxEntries) _cache[hex] = brush;

        return brush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A brush is not a colour the backend can take.");
}
