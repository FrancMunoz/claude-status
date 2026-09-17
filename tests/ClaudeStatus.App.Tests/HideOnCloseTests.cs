using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using ClaudeStatus.App.Views;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Reused windows hide when the user closes them, and never refuse a shutdown.
/// </summary>
/// <remarks>
/// The failure this guards: a hidden Config window cancelled every non-forced
/// shutdown, so a quit from macOS left the app torn down but running.
/// </remarks>
[Collection(HeadlessTests.Name)]
[SuppressMessage("Performance", "CA1801:Review unused parameters",
    Justification = "The fixture parameter forces xUnit to start the Avalonia session.")]
public class HideOnCloseTests(HeadlessAppFixture fixture)
{
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, true)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, true)]
    [InlineData(WindowCloseReason.Undefined, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    public void Only_a_shutdown_really_closes_the_window(WindowCloseReason reason, bool hides)
        => HideOnClose.ShouldHide(reason).Should().Be(hides);

    [Fact]
    public void Closing_the_window_hides_it_and_keeps_it_for_reuse()
    {
        (bool Visible, bool Closed) after = HeadlessAppFixture.Invoke(() =>
        {
            var window = new Window();
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            HideOnClose.Attach(window);

            window.Show();
            window.Close();

            return (window.IsVisible, closed);
        });

        after.Visible.Should().BeFalse("closing hides it");
        after.Closed.Should().BeFalse("it is kept, not closed, so reopening is instant");
    }

    [Fact]
    public void The_fixture_is_what_boots_Avalonia_for_this_collection()
        => fixture.Should().NotBeNull();
}
