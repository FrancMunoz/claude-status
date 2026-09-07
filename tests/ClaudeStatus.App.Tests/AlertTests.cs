using ClaudeStatus.App.Tray;
using ClaudeStatus.Platform;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// How the tray reacts to a credential problem, which is the one failure the
/// user has to act on.
/// </summary>
[Collection(HeadlessTests.Name)]
public class AlertTests(HeadlessAppFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly ClaudeStatus.Localization.Localizer L = TestLocalizer.English();

    [Fact]
    public void A_missing_credential_renders_an_attention_glyph_not_a_percentage()
    {
        fixture.Should().NotBeNull();

        HeadlessAppFixture.Invoke(() =>
        {
            using var bitmap = TrayIconRenderer.Render(
                null, IndicatorMode.SessionPercent, ThresholdState.Unknown,
                IndicatorAlert.NeedsCredential);

            bitmap.Should().NotBeNull();
        });
    }

    [Fact]
    public void The_attention_glyph_overrides_the_ring_mode_too()
    {
        // Otherwise a user in Ring mode gets an empty circle and no explanation.
        HeadlessAppFixture.Invoke(() =>
        {
            using var bitmap = TrayIconRenderer.Render(
                null, IndicatorMode.Ring, ThresholdState.Unknown, IndicatorAlert.NeedsCredential);

            bitmap.Should().NotBeNull();
        });
    }

    [Fact]
    public void The_tooltip_for_a_missing_credential_says_what_to_do()
    {
        string tooltip = TrayIconIndicator.BuildTooltip(
            L, null, IndicatorMode.SessionPercent, IndicatorAlert.NeedsCredential);

        tooltip.Should().Contain("credential");
        tooltip.Should().Contain("Config", "the user needs to know where to go");
    }

    [Fact]
    public void The_tooltip_distinguishes_unreachable_from_no_data_yet()
    {
        string unreachable = TrayIconIndicator.BuildTooltip(
            L, null, IndicatorMode.SessionPercent, IndicatorAlert.Unreachable);
        string noData = TrayIconIndicator.BuildTooltip(
            L, null, IndicatorMode.SessionPercent, IndicatorAlert.None);

        unreachable.Should().NotBe(noData);
        unreachable.Should().Contain("reach");
    }

    [Fact]
    public void A_credential_alert_still_shows_cached_numbers_in_the_tooltip()
    {
        // Only the icon is hijacked; the tooltip keeps whatever we last knew.
        var snapshot = new UsageSnapshot(
            Session: UsageWindow.Create(29d, Now),
            Week: UsageWindow.Create(58d, Now),
            WeekFable: null,
            OtherWindows: new Dictionary<string, UsageWindow>(),
            FetchedAt: Now,
            IsStale: true);

        TrayIconIndicator.BuildTooltip(L, snapshot, IndicatorMode.SessionPercent)
            .Should().Contain("29");
    }
}
