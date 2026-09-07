using Avalonia;
using ClaudeStatus.App.Tray;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Where the details popup lands.
/// </summary>
/// <remarks>
/// The bug these exist for: window sizes are device-independent units, but
/// <c>Screen.WorkingArea</c> and <c>Window.Position</c> are physical pixels. Treating
/// one as the other put a third of the window off the right edge of a scaled display,
/// and left it overlapping the taskbar at the bottom.
/// </remarks>
public class TrayPopupPlacementTests
{
    /// <summary>A 1920x1080 screen with a 48 px taskbar along the bottom.</summary>
    private static readonly PixelRect Bounds = new(0, 0, 1920, 1080);
    private static readonly PixelRect Bottom = new(0, 0, 1920, 1032);

    private static readonly Size Popup = new(340, 420);

    /// <summary>The rectangle the popup would occupy, in physical pixels.</summary>
    private static PixelRect Placed(
        PixelRect bounds, PixelRect working, Size size, double scaling)
    {
        PixelPoint at = TrayPopupPlacement.Place(bounds, working, size, scaling);
        return new PixelRect(
            at.X,
            at.Y,
            (int)Math.Ceiling(size.Width * scaling),
            (int)Math.Ceiling(size.Height * scaling));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void The_whole_window_stays_inside_the_working_area_at_any_scaling(double scaling)
    {
        // The original failure was at scaling > 1: the window was positioned as if
        // 340 units meant 340 pixels, so it hung off the right edge by the difference.
        PixelRect placed = Placed(Bounds, Bottom, Popup, scaling);

        placed.X.Should().BeGreaterThanOrEqualTo(Bottom.X);
        placed.Y.Should().BeGreaterThanOrEqualTo(Bottom.Y);
        placed.Right.Should().BeLessThanOrEqualTo(Bottom.Right, "the right edge must not run off screen");
        placed.Bottom.Should().BeLessThanOrEqualTo(Bottom.Bottom, "it must not sit under the taskbar");
    }

    [Fact]
    public void A_bottom_taskbar_anchors_the_popup_to_the_bottom_right()
    {
        PixelRect placed = Placed(Bounds, Bottom, Popup, 1.0);

        placed.Right.Should().BeInRange(Bottom.Right - 20, Bottom.Right);
        placed.Bottom.Should().BeInRange(Bottom.Bottom - 20, Bottom.Bottom);
    }

    [Fact]
    public void A_top_bar_anchors_the_popup_below_it_not_underneath_it()
    {
        // macOS: the menu bar is at the top, so hanging the popup off the bottom of
        // the screen would put it as far from the status item as possible.
        var underMenuBar = new PixelRect(0, 25, 1920, 1055);

        PixelRect placed = Placed(Bounds, underMenuBar, Popup, 1.0);

        placed.Y.Should().BeGreaterThanOrEqualTo(underMenuBar.Y, "it must clear the menu bar");
        placed.Y.Should().BeLessThan(200, "it should hang from the top, near the status item");
        placed.Right.Should().BeInRange(underMenuBar.Right - 20, underMenuBar.Right);
    }

    [Fact]
    public void A_left_dock_anchors_the_popup_to_that_side()
    {
        var besideDock = new PixelRect(64, 0, 1856, 1080);

        PixelRect placed = Placed(Bounds, besideDock, Popup, 1.0);

        placed.X.Should().BeInRange(besideDock.X, besideDock.X + 20);
    }

    [Fact]
    public void A_screen_offset_in_the_virtual_desktop_is_respected()
    {
        // A second monitor to the left of the primary one has a negative origin, which
        // is the classic way an "always positive" placement lands on the wrong screen.
        var secondary = new PixelRect(-1920, -200, 1920, 1080);
        var working = new PixelRect(-1920, -200, 1920, 1032);

        PixelRect placed = Placed(secondary, working, Popup, 1.0);

        placed.X.Should().BeGreaterThanOrEqualTo(working.X);
        placed.Right.Should().BeLessThanOrEqualTo(working.Right);
        placed.Y.Should().BeGreaterThanOrEqualTo(working.Y);
        placed.Bottom.Should().BeLessThanOrEqualTo(working.Bottom);
    }

    [Fact]
    public void A_popup_taller_than_the_screen_is_pinned_to_the_top_not_pushed_off_it()
    {
        var small = new PixelRect(0, 0, 1024, 600);
        var working = new PixelRect(0, 0, 1024, 560);

        PixelPoint at = TrayPopupPlacement.Place(small, working, new Size(340, 900), 1.0);

        at.Y.Should().Be(working.Y, "the top of the content must remain reachable");
        at.X.Should().BeGreaterThanOrEqualTo(working.X);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void A_nonsensical_scaling_falls_back_to_one_instead_of_parking_it_offscreen(double scaling)
    {
        PixelPoint at = TrayPopupPlacement.Place(Bounds, Bottom, Popup, scaling);

        at.X.Should().BeInRange(Bottom.X, Bottom.Right);
        at.Y.Should().BeInRange(Bottom.Y, Bottom.Bottom);
    }

    [Fact]
    public void An_unmeasured_window_still_gets_a_sane_position()
    {
        // Before the first layout pass a SizeToContent window reports NaN.
        PixelPoint at = TrayPopupPlacement.Place(
            Bounds, Bottom, new Size(double.NaN, double.NaN), 1.5);

        at.X.Should().BeInRange(Bottom.X, Bottom.Right);
        at.Y.Should().BeInRange(Bottom.Y, Bottom.Bottom);
    }
}
