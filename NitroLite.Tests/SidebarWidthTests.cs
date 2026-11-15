using NitroLite.Core;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// The left rail is resizable, so its width is a persisted number that arrives from a file the user can edit.
/// <see cref="AppSettings.ClampSidebarWidth"/> is the only thing standing between a hand-edited value and a window
/// with no content area, which is why it is tested on its own rather than through the window.
/// </summary>
public class SidebarWidthTests
{
    [Fact]
    public void Default_IsInsideTheAllowedRange()
    {
        Assert.InRange(AppSettings.DefaultSidebarWidth, AppSettings.MinSidebarWidth, AppSettings.MaxSidebarWidth);
    }

    [Theory]
    [InlineData(175)]
    [InlineData(215)]
    [InlineData(290)]
    public void AWidthInsideTheRange_IsKept(double width)
    {
        Assert.Equal(width, AppSettings.ClampSidebarWidth(width));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    [InlineData(120)]
    public void AWidthBelowTheMinimum_BecomesTheMinimum(double width)
    {
        Assert.Equal(AppSettings.MinSidebarWidth, AppSettings.ClampSidebarWidth(width));
    }

    [Theory]
    [InlineData(291)]
    [InlineData(900)]
    [InlineData(double.MaxValue)]
    public void AWidthAboveTheMaximum_BecomesTheMaximum(double width)
    {
        Assert.Equal(AppSettings.MaxSidebarWidth, AppSettings.ClampSidebarWidth(width));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANonFiniteWidth_FallsBackToTheDefault(double width)
    {
        // Math.Clamp throws on NaN, and a JSON file can carry "NaN" or "Infinity" as a bare token.
        Assert.Equal(AppSettings.DefaultSidebarWidth, AppSettings.ClampSidebarWidth(width));
    }

    [Fact]
    public void TheDefault_IsWhatTheSplitterResetsTo()
    {
        // The double-click reset writes DefaultSidebarWidth straight into the column, so the constant has to be a
        // width the clamp would also accept; otherwise a reset would be silently corrected on the next read.
        Assert.Equal(AppSettings.DefaultSidebarWidth,
            AppSettings.ClampSidebarWidth(AppSettings.DefaultSidebarWidth));
    }
}
