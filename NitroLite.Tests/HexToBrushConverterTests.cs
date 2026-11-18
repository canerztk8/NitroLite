using System.Globalization;
using Avalonia.Media;
using NitroLite.Desktop.Converters;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The hex-to-brush converter is on the lighting page's binding path, so it runs again on every pass over four zone
/// previews, the palette and the active colour.
/// </summary>
/// <remarks>
/// The cache is the point of these tests, and so is its bound: a colour picker's text box produces a new string per
/// keystroke, and an unbounded dictionary would keep every one of them for the life of the process.
/// </remarks>
public class HexToBrushConverterTests
{
    private static object? Convert(HexToBrushConverter converter, object? value) =>
        converter.Convert(value, typeof(IBrush), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_ParsesAHexString()
    {
        var converter = new HexToBrushConverter();

        var brush = Assert.IsType<SolidColorBrush>(Convert(converter, "#FF8800"));

        Assert.Equal(Color.Parse("#FF8800"), brush.Color);
    }

    [Fact]
    public void Convert_AcceptsAHexWithoutTheHash()
    {
        var converter = new HexToBrushConverter();

        var brush = Assert.IsType<SolidColorBrush>(Convert(converter, "00FF00"));

        Assert.Equal(Color.Parse("#00FF00"), brush.Color);
    }

    [Fact]
    public void Convert_ReturnsTheSameBrushForTheSameColour()
    {
        var converter = new HexToBrushConverter();

        var first = Convert(converter, "#2F81F7");
        var second = Convert(converter, "#2F81F7");

        // The whole point of the cache: a binding pass that re-evaluates the same colour must not allocate a brush.
        Assert.Same(first, second);
        Assert.Equal(1, converter.CachedCount);
    }

    [Fact]
    public void Convert_TreatsTheHexCaseInsensitively()
    {
        var converter = new HexToBrushConverter();

        var lower = Convert(converter, "#aabbcc");
        var upper = Convert(converter, "#AABBCC");

        Assert.Same(lower, upper);
        Assert.Equal(1, converter.CachedCount);
    }

    [Fact]
    public void Convert_TrimsSurroundingWhitespace()
    {
        var converter = new HexToBrushConverter();

        var padded = Convert(converter, "  #123456  ");
        var plain = Convert(converter, "#123456");

        Assert.Same(padded, plain);
    }

    [Fact]
    public void Convert_UnparseableValuePaintsNothingAndIsNotCached()
    {
        var converter = new HexToBrushConverter();

        // A half-typed hex in the text box: the preview goes blank rather than the window going down.
        Assert.Same(Brushes.Transparent, Convert(converter, "#12"));
        Assert.Same(Brushes.Transparent, Convert(converter, "not a colour"));
        Assert.Same(Brushes.Transparent, Convert(converter, string.Empty));
        Assert.Same(Brushes.Transparent, Convert(converter, null));
        Assert.Same(Brushes.Transparent, Convert(converter, 42));

        Assert.Equal(0, converter.CachedCount);
    }

    [Fact]
    public void Convert_StopsCachingAtTheBound()
    {
        var converter = new HexToBrushConverter();

        // More distinct colours than the cache holds, which is what a long session in the colour picker looks like.
        for (var i = 0; i < 400; i++)
        {
            var hex = "#" + (i * 7919 % 0x1000000).ToString("X6", CultureInfo.InvariantCulture);
            Assert.IsType<SolidColorBrush>(Convert(converter, hex));
        }

        Assert.True(converter.CachedCount <= 256, $"cache grew to {converter.CachedCount} entries");

        // The bound is a cache limit, not a correctness limit: a colour that fell out is converted again, not refused.
        Assert.IsType<SolidColorBrush>(Convert(converter, "#FF8800"));
    }

    [Fact]
    public void ConvertBack_RefusesBecauseABrushIsNotAProtocolValue()
    {
        var converter = new HexToBrushConverter();

        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Brushes.Red, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
