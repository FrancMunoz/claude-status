using ClaudeStatus.App.Tray;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Left-clicking the tray icon toggles the popup.
/// </summary>
/// <remarks>
/// The whole reason this rule is not <c>IsVisible ? Hide : Show</c> is that the
/// popup hides itself on losing focus, so a tray click has already dismissed it
/// before the handler runs. These tests pin the timing that distinguishes "the
/// click that closed it" from "a later click asking to open it again" - a
/// distinction that is impossible to verify by clicking around, because getting
/// it wrong looks exactly like the icon ignoring you.
/// </remarks>
public class TrayPopupToggleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void A_click_while_the_popup_is_visible_closes_it()
    {
        // The straightforward half, and the one that happens on backends that do
        // not deactivate the window before delivering the click.
        TrayPopupToggle.Decide(isVisible: true, TimeSpan.FromSeconds(10), Window)
            .Should().Be(PopupClickResult.Hide);
    }

    [Fact]
    public void A_click_with_the_popup_closed_opens_it()
    {
        TrayPopupToggle.Decide(isVisible: false, TimeSpan.FromSeconds(10), Window)
            .Should().Be(PopupClickResult.Show);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(249)]
    public void A_click_that_arrives_with_the_dismissal_does_not_reopen(int millisecondsAfter)
    {
        // Without this the popup reopens the instant the user clicks to close it,
        // and the icon appears not to respond at all.
        TrayPopupToggle.Decide(
            isVisible: false, TimeSpan.FromMilliseconds(millisecondsAfter), Window)
            .Should().Be(PopupClickResult.Ignore);
    }

    [Theory]
    [InlineData(250)]
    [InlineData(400)]
    [InlineData(5000)]
    public void A_click_after_the_window_has_passed_opens_it_again(int millisecondsAfter)
    {
        TrayPopupToggle.Decide(
            isVisible: false, TimeSpan.FromMilliseconds(millisecondsAfter), Window)
            .Should().Be(PopupClickResult.Show);
    }

    [Fact]
    public void The_popup_has_never_been_dismissed_on_the_first_ever_click()
    {
        // The dismissal stamp starts at DateTimeOffset.MinValue, which makes the
        // elapsed time enormous. That must read as "long ago", not overflow into
        // something that suppresses the very first click.
        TimeSpan sinceEpoch = DateTimeOffset.UtcNow - DateTimeOffset.MinValue;

        TrayPopupToggle.Decide(isVisible: false, sinceEpoch, Window)
            .Should().Be(PopupClickResult.Show);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100000)]
    public void A_clock_that_went_backwards_still_opens_the_popup(int millisecondsAfter)
    {
        // Resume from sleep, or an NTP correction. Refusing to open the window is
        // a worse failure than opening one the user meant to close, so a negative
        // age is treated as "long ago" rather than as "just now".
        TrayPopupToggle.Decide(
            isVisible: false, TimeSpan.FromMilliseconds(millisecondsAfter), Window)
            .Should().Be(PopupClickResult.Show);
    }

    [Fact]
    public void The_shipped_suppression_window_is_long_enough_to_cover_a_mouse_press()
    {
        // Deactivation and click are two halves of one press, milliseconds apart.
        // Far shorter than this and the guard stops working on a slow machine;
        // far longer and a deliberate second click starts being swallowed.
        TrayPopupToggle.ReopenSuppression.Should().BeGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(100));
        TrayPopupToggle.ReopenSuppression.Should().BeLessThanOrEqualTo(
            TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void The_default_overload_uses_the_shipped_window()
    {
        TrayPopupToggle.Decide(isVisible: false, TimeSpan.Zero)
            .Should().Be(PopupClickResult.Ignore);
        TrayPopupToggle.Decide(isVisible: false, TimeSpan.FromSeconds(1))
            .Should().Be(PopupClickResult.Show);
    }
}
