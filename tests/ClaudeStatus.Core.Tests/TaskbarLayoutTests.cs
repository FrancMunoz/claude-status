using ClaudeStatus.Platform;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Where the taskbar widget lands. The platform measures; this arithmetic
/// decides; a wrong answer here is a widget on top of the clock.
/// </summary>
public class TaskbarLayoutTests
{
    /// <summary>A 1920-wide, 48-tall taskbar at the bottom of a 1080 screen.</summary>
    private static readonly PixelBox Bottom = new(0, 1032, 1920, 1080);

    /// <summary>A tray of the usual sort: 200 px, flush with the right edge.</summary>
    private static readonly PixelBox Tray = new(1720, 1032, 1920, 1080);

    [Fact]
    public void The_widget_sits_just_left_of_the_tray_and_spans_the_full_height()
    {
        TaskbarSlot? slot = TaskbarLayout.Compute(new TaskbarMetrics(Bottom, Tray, 96), 180);

        slot.Should().NotBeNull();
        slot!.X.Should().Be(1720 - TaskbarLayout.Gap - 180);
        slot.Y.Should().Be(0);
        slot.Width.Should().Be(180);
        slot.Height.Should().Be(48, "it fills the taskbar and centres its content itself");
    }

    [Fact]
    public void Positions_are_relative_to_the_taskbar_not_the_screen()
    {
        // A taskbar on a second monitor to the right of the first.
        var bar = new PixelBox(1920, 1032, 3840, 1080);
        var tray = new PixelBox(3640, 1032, 3840, 1080);

        TaskbarSlot? slot = TaskbarLayout.Compute(new TaskbarMetrics(bar, tray, 96), 100);

        slot!.X.Should().Be(1720 - TaskbarLayout.Gap - 100, "SetWindowPos on a child takes parent coordinates");
    }

    [Fact]
    public void Without_a_measurable_tray_it_leaves_room_for_the_clock()
    {
        TaskbarSlot? slot = TaskbarLayout.Compute(new TaskbarMetrics(Bottom, null, 96), 180);

        slot!.X.Should().Be(1920 - TaskbarLayout.FallbackTrayWidth - TaskbarLayout.Gap - 180);
    }

    [Fact]
    public void An_empty_tray_rectangle_counts_as_unmeasured()
    {
        // GetWindowRect on a hidden tray window returns a zero-sized box, not a
        // failure, and a widget placed against x=0 would sit on the Start button.
        var empty = new PixelBox(0, 0, 0, 0);

        TaskbarSlot? withEmpty = TaskbarLayout.Compute(new TaskbarMetrics(Bottom, empty, 96), 180);
        TaskbarSlot? withNone = TaskbarLayout.Compute(new TaskbarMetrics(Bottom, null, 96), 180);

        withEmpty.Should().Be(withNone);
    }

    [Fact]
    public void The_gap_and_the_fallback_scale_with_the_taskbar_dpi()
    {
        TaskbarSlot? at96 = TaskbarLayout.Compute(new TaskbarMetrics(Bottom, null, 96), 100);
        TaskbarSlot? at192 = TaskbarLayout.Compute(new TaskbarMetrics(Bottom, null, 192), 100);

        (at96!.X - at192!.X).Should().Be(
            TaskbarLayout.FallbackTrayWidth + TaskbarLayout.Gap,
            "at 200 % both reservations double, so the widget moves left by one more of each");
    }

    [Fact]
    public void A_vertical_taskbar_is_not_supported_yet()
    {
        var left = new PixelBox(0, 0, 62, 1080);

        TaskbarLayout.Compute(new TaskbarMetrics(left, null, 96), 100).Should().BeNull();
    }

    [Fact]
    public void A_taskbar_too_narrow_for_the_widget_gets_no_widget()
    {
        // Rather than a widget clipped at the left edge or drawn over Start.
        var tiny = new PixelBox(0, 1032, 300, 1080);

        TaskbarLayout.Compute(new TaskbarMetrics(tiny, null, 96), 250).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void A_widget_with_no_width_has_no_slot(int width)
    {
        TaskbarLayout.Compute(new TaskbarMetrics(Bottom, Tray, 96), width).Should().BeNull();
    }

    [Fact]
    public void A_degenerate_taskbar_yields_nothing_rather_than_throwing()
    {
        var gone = new PixelBox(0, 0, 0, 0);

        TaskbarLayout.Compute(new TaskbarMetrics(gone, null, 96), 100).Should().BeNull();
    }

    [Fact]
    public void Orientation_is_judged_from_the_shape()
    {
        new TaskbarMetrics(Bottom, null, 96).IsHorizontal.Should().BeTrue();
        new TaskbarMetrics(new PixelBox(0, 0, 62, 1080), null, 96).IsHorizontal.Should().BeFalse();
    }

    [Fact]
    public void The_unsupported_host_is_inert()
    {
        var host = new UnsupportedTaskbarHost();

        host.IsSupported.Should().BeFalse();
        host.Measure().Should().BeNull();
        host.Attach(1).Should().BeFalse();
        host.IsAttached(1).Should().BeFalse();

        Action move = () => host.Move(1, new TaskbarSlot(0, 0, 1, 1));
        Action detach = () => host.Detach(1);
        move.Should().NotThrow();
        detach.Should().NotThrow();
    }
}
