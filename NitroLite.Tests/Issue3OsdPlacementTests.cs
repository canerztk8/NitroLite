using Avalonia;
using NitroLite.Desktop.Views;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Issue 3, item B: on a 2560x1600 display at 150 % the right-hand OSD corners hung off the screen. The placement is a
/// pure function of the working area, the scale and the frame's size, so it is tested without a window.
/// </summary>
public class Issue3OsdPlacementTests
{
    private const double Margin = 18;

    // 2560x1600 at 150 % is the same physical area; the frame size is in DIPs.
    private static readonly PixelRect Screen = new(0, 0, 2560, 1600);
    private static readonly Size Frame = new(200, 60);

    private static PixelPoint At(PixelRect area, double scale, Size frame, int corner)
        => OsdOverlayWindow.ComputePosition(area, scale, frame, corner, Margin);

    [Fact]
    public void Top_left_is_the_margin_inside_the_origin()
    {
        Assert.Equal(new PixelPoint(27, 27), At(Screen, 1.5, Frame, 0));
    }

    [Fact]
    public void Top_right_ends_a_margin_before_the_right_edge()
    {
        // 200 * 1.5 = 300 wide, 18 * 1.5 = 27 margin.
        Assert.Equal(new PixelPoint(2560 - 300 - 27, 27), At(Screen, 1.5, Frame, 1));
    }

    [Fact]
    public void Bottom_left_ends_a_margin_before_the_bottom_edge()
    {
        Assert.Equal(new PixelPoint(27, 1600 - 90 - 27), At(Screen, 1.5, Frame, 2));
    }

    [Fact]
    public void Bottom_right_ends_a_margin_before_both_edges()
    {
        Assert.Equal(new PixelPoint(2560 - 300 - 27, 1600 - 90 - 27), At(Screen, 1.5, Frame, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_frame_that_grew_wider_stays_inside_the_right_edge(int corner)
    {
        var narrow = At(Screen, 1.5, new Size(200, 60), corner);
        var wide = At(Screen, 1.5, new Size(420, 60), corner);

        Assert.True(wide.X + 420 * 1.5 <= Screen.Right);
        if (corner is 1 or 3) Assert.True(wide.X < narrow.X); // it moved left to make room
        else Assert.Equal(narrow.X, wide.X); // the left corners do not depend on the width
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_frame_wider_than_the_screen_starts_at_the_left_edge(int corner)
    {
        var position = At(Screen, 1.5, new Size(3000, 60), corner);

        Assert.True(position.X >= Screen.X);
        if (corner is 1 or 3) Assert.Equal(Screen.X, position.X);
    }

    [Fact]
    public void A_frame_taller_than_the_screen_starts_at_the_top_edge()
    {
        Assert.Equal(0, At(Screen, 1.5, new Size(200, 2000), 3).Y);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Every_scale_keeps_every_corner_inside_the_area(double scale)
    {
        for (int corner = 0; corner < 4; corner++)
        {
            var p = At(Screen, scale, Frame, corner);

            Assert.True(p.X >= Screen.X && p.Y >= Screen.Y);
            Assert.True(p.X + Frame.Width * scale <= Screen.Right);
            Assert.True(p.Y + Frame.Height * scale <= Screen.Bottom);
        }
    }

    [Fact]
    public void Scale_one_uses_the_margin_unscaled()
    {
        Assert.Equal(new PixelPoint(2560 - 200 - 18, 1600 - 60 - 18), At(Screen, 1.0, Frame, 3));
    }

    [Fact]
    public void A_non_positive_scale_is_treated_as_one()
    {
        Assert.Equal(At(Screen, 1.0, Frame, 3), At(Screen, 0, Frame, 3));
    }

    [Fact]
    public void A_secondary_monitor_with_a_non_zero_origin_is_placed_relative_to_it()
    {
        // A 1920x1080 display to the right of the primary, with a 40 px taskbar taken off the bottom.
        var secondary = new PixelRect(2560, 0, 1920, 1040);
        var p = At(secondary, 1.0, Frame, 3);

        Assert.Equal(new PixelPoint(2560 + 1920 - 200 - 18, 1040 - 60 - 18), p);
        Assert.Equal(new PixelPoint(2560 + 18, 18), At(secondary, 1.0, Frame, 0));
    }

    [Fact]
    public void A_monitor_to_the_left_with_a_negative_origin_clamps_to_its_own_edge()
    {
        var left = new PixelRect(-1920, -200, 1920, 1080);

        Assert.Equal(new PixelPoint(-1920 + 18, -200 + 18), At(left, 1.0, Frame, 0));
        Assert.Equal(new PixelPoint(-1920, -200), At(left, 1.0, new Size(5000, 5000), 3));
    }
}
