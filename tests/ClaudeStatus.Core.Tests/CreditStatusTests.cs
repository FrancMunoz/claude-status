using ClaudeStatus.Export;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Whether usage is being paid for, read from <c>usage-credits.json</c> - a real
/// capture of an account on credits with the week spent.
/// </summary>
public class CreditStatusTests
{
    private static UsageSnapshot OnCredits() => Fixture.Parse(Fixture.Credits);

    private static readonly MoneyAmount Spent = new(562, "EUR", 2);
    private static readonly MoneyAmount Cap = new(10000, "EUR", 2);

    private static UsageSnapshot Snapshot(
        double session = 10d,
        double week = 20d,
        double? fable = null,
        SpendInfo? spend = null,
        ExtraUsageInfo? extra = null) => new(
            UsageWindow.Create(session, null),
            UsageWindow.Create(week, null),
            fable is { } f ? UsageWindow.Create(f, null) : null,
            new Dictionary<string, UsageWindow>(),
            Fixture.FixedNow,
            IsStale: false)
        {
            Spend = spend,
            ExtraUsage = extra,
        };

    private static SpendInfo SpendOn(MoneyAmount? used = null, MoneyAmount? limit = null)
        => new(true, used ?? Spent, limit ?? Cap, 6d, "normal", null);

    [Fact]
    public void The_capture_reads_as_in_use_with_what_is_spent_and_left()
    {
        CreditStatus credits = CreditStatus.From(OnCredits())!;

        credits.State.Should().Be(CreditState.InUse, "the week is at 100 % and credits are on");
        credits.Used.Should().Be(Spent);
        credits.Limit.Should().Be(Cap);
        credits.Remaining.Should().Be(new MoneyAmount(9438, "EUR", 2));
        credits.Percent.Should().BeApproximately(5.62, 1e-9, "worked out, not the endpoint's rounded 6");
        credits.IsPastLimits.Should().BeTrue();
    }

    [Fact]
    public void Extra_usage_figures_are_minor_units_beside_their_decimal_places()
    {
        ExtraUsageInfo extra = OnCredits().ExtraUsage!;

        extra.DecimalPlaces.Should().Be(2);
        extra.ToMoney(extra.UsedCredits).Should().Be(Spent, "562 with two places is 5.62, as spend.used says");
        extra.ToMoney(extra.MonthlyLimit).Should().Be(Cap);
    }

    [Fact]
    public void Credits_switched_off_are_not_mentioned()
    {
        CreditStatus.From(Snapshot(week: 100d, spend: SpendOn() with { Enabled = false })).Should().BeNull();
        CreditStatus.From(Snapshot(week: 100d)).Should().BeNull("the source reported no credits at all");
        CreditStatus.From(null).Should().BeNull();
    }

    [Fact]
    public void Credits_on_with_every_limit_in_hand_are_on_standby()
        => CreditStatus.From(Snapshot(session: 99.9d, week: 80d, spend: SpendOn()))!
            .State.Should().Be(CreditState.Standby, "99.9 rounds to 100 but is not spent");

    [Theory]
    [InlineData(100d, 20d, null)]
    [InlineData(10d, 100d, null)]
    [InlineData(10d, 20d, 100d)]
    public void Any_spent_window_puts_usage_on_credits(double session, double week, double? fable)
        => CreditStatus.From(Snapshot(session, week, fable, SpendOn()))!
            .State.Should().Be(CreditState.InUse);

    [Fact]
    public void The_cap_is_reached_when_the_source_says_so_or_the_spend_meets_it()
    {
        var flagged = new ExtraUsageInfo(true, false, true, true, 10000d, 9000d, 90d, "EUR", null);
        CreditStatus.From(Snapshot(week: 100d, extra: flagged))!.State.Should().Be(CreditState.CapReached);

        CreditStatus.From(Snapshot(week: 100d, spend: SpendOn(used: Cap)))!
            .State.Should().Be(CreditState.CapReached, "the whole cap is spent even if no flag says so");
    }

    [Fact]
    public void Extra_usage_stands_in_when_there_is_no_spend_block()
    {
        var extra = new ExtraUsageInfo(true, false, false, true, 10000d, 562d, 5.62d, "EUR", null);

        CreditStatus credits = CreditStatus.From(Snapshot(week: 100d, extra: extra))!;

        credits.Used.Should().Be(Spent, "two decimal places are assumed when none are given");
        credits.Limit.Should().Be(Cap);
    }

    [Fact]
    public void Nothing_is_left_or_a_share_without_a_cap_or_across_currencies()
    {
        CreditStatus noCap = CreditStatus.From(Snapshot(spend: new SpendInfo(true, Spent, null, null, null, null)))!;
        noCap.Remaining.Should().BeNull();
        noCap.Percent.Should().BeNull();

        CreditStatus mixed = CreditStatus.From(Snapshot(spend: SpendOn(limit: new MoneyAmount(10000, "USD", 2))))!;
        mixed.Remaining.Should().BeNull("euros cannot be taken from dollars");
    }

    [Fact]
    public void Overspending_leaves_nothing_rather_than_a_negative()
        => CreditStatus.From(Snapshot(spend: SpendOn(used: new MoneyAmount(12000, "EUR", 2))))!
            .Remaining!.AmountMinor.Should().Be(0);

    [Theory]
    [InlineData("EUR", "€5.62")]
    [InlineData("usd", "$5.62")]
    [InlineData("CHF", "5.62 CHF")]
    [InlineData(null, "5.62")]
    public void The_compact_amount_puts_a_one_glyph_symbol_first_and_a_code_last(string? currency, string expected)
        => CreditStatus.Compact(new MoneyAmount(562, currency, 2)).Should().Be(expected);

    [Fact]
    public void The_menu_bar_row_adds_the_amount_only_while_past_the_limits()
    {
        IndicatorText.ComposeRow(OnCredits(), IndicatorAlert.None, ("5h", "7d", "F"), false)
            .Should().Be("5h 11% · 7d x · €5.62");

        IndicatorText.ComposeRow(Snapshot(spend: SpendOn()), IndicatorAlert.None, ("5h", "7d", "F"), false)
            .Should().Be("5h 10% · 7d 20%", "on standby credits are not news");

        IndicatorText.CreditsToken(OnCredits(), IndicatorAlert.Unreachable)
            .Should().BeEmpty("an alert replaces the readings, credits included");
    }

    [Fact]
    public void The_export_carries_the_verdict_and_the_amounts_in_minor_units()
    {
        UsageExport export = UsageExportMapper.Map(
            OnCredits(), IndicatorAlert.None, null, null, 80d, Fixture.FixedNow, "1.0.0");

        export.Credits.Should().Be(new UsageExportCredits(CreditState.InUse, 562, 10000, "EUR", 2));
    }

    [Fact]
    public void The_export_says_nothing_about_credits_that_are_off()
        => UsageExportMapper.Map(Snapshot(), IndicatorAlert.None, null, null, 80d, Fixture.FixedNow, "1.0.0")
            .Credits.Should().BeNull();
}
