using ClaudeStatus.App.Tray;
using ClaudeStatus.App.ViewModels;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// The card an icon borrows to say something, and the two indicators that had no
/// surface of their own before it.
/// </summary>
/// <remarks>
/// <c>ShowNotice</c> is where the velocity warning reaches the user. It was
/// implemented only on the taskbar widget, so on Linux, on macOS and on any
/// Windows machine falling back to the tray icon the warning was the interface's
/// no-op default and arrived nowhere.
/// </remarks>
[Collection(HeadlessTests.Name)]
public class UsageNoticeCardTests(HeadlessAppFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot() => new(
        Session: UsageWindow.Create(72d, Now.AddHours(1)),
        Week: UsageWindow.Create(44d, Now.AddDays(3)),
        WeekFable: UsageWindow.Create(12d, Now.AddDays(3)),
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: Now,
        IsStale: false);

    [Fact]
    public void The_fixture_is_what_boots_Avalonia_for_this_collection()
        => fixture.Should().NotBeNull();

    [Fact]
    public void A_notice_puts_the_card_on_screen_with_the_sentence_on_it()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
            viewModel.Update(Snapshot(), IndicatorAlert.None, Now);

            using var card = new UsageNoticeCard(viewModel, anchorAtTop: false);
            card.IsShowing.Should().BeFalse("nothing has been said yet");

            card.Show("Session runs out in 20 minutes.", TimeSpan.FromSeconds(8));

            card.IsShowing.Should().BeTrue();
            viewModel.NoticeText.Should().Be("Session runs out in 20 minutes.");
            viewModel.HasNotice.Should().BeTrue();
        });
    }

    [Fact]
    public void An_empty_notice_takes_the_card_back_down()
    {
        // How the controller says the pace has come back to normal. A card holding
        // an empty warning panel would be a rectangle with nothing in it.
        HeadlessAppFixture.Invoke(() =>
        {
            var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
            viewModel.Update(Snapshot(), IndicatorAlert.None, Now);

            using var card = new UsageNoticeCard(viewModel, anchorAtTop: false);
            card.Show("Session runs out in 20 minutes.", TimeSpan.FromSeconds(8));
            card.Show(string.Empty, TimeSpan.FromSeconds(8));

            card.IsShowing.Should().BeFalse();
            viewModel.HasNotice.Should().BeFalse();
        });
    }

    [Fact]
    public void A_disposed_card_says_nothing()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
            var card = new UsageNoticeCard(viewModel, anchorAtTop: false);
            card.Dispose();

            card.Show("too late", TimeSpan.FromSeconds(8));

            card.IsShowing.Should().BeFalse();
        });
    }

    [Fact]
    public void The_tray_icon_shows_a_notice_rather_than_swallowing_it()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            using var indicator = new TrayIconIndicator(TestLocalizer.English());
            indicator.Configure(new IndicatorOptions(
                ThresholdPercent: 80d,
                ShowWeekFable: false,
                PollInterval: TimeSpan.FromMinutes(1)));
            indicator.Render(Snapshot(), IndicatorMode.SessionPercent, ThresholdState.Normal, IndicatorAlert.None);

            // No exception, and no return value to assert on: the interface's
            // default implementation is a no-op, so the thing being proved is that
            // this type overrides it at all. The card itself is covered above.
            indicator.ShowNotice("Session runs out in 20 minutes.", TimeSpan.FromSeconds(8));

            typeof(TrayIconIndicator)
                .GetMethod(nameof(IStatusIndicator.ShowNotice))!
                .DeclaringType.Should().Be<TrayIconIndicator>("the icon must not inherit the no-op");
        });
    }
}
