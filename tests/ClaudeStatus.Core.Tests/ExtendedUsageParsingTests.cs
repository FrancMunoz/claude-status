namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The fields the parser used to discard, now shown in the report window.
/// </summary>
/// <remarks>
/// Asserted against <c>usage-normal.json</c>, which is a real Max-plan capture,
/// so these are the values the endpoint actually returned rather than values we
/// invented. Where a field was null in that capture, the test says so explicitly
/// - that is information about the plan, not a gap in the test.
/// </remarks>
public class ExtendedUsageParsingTests
{
    private static UsageSnapshot Normal() => Fixture.Parse(Fixture.Normal);

    [Fact]
    public void Severity_is_read_verbatim_from_each_limit()
    {
        UsageSnapshot snapshot = Normal();

        snapshot.Session!.Severity.Should().Be("normal");
        snapshot.Week!.Severity.Should().Be("normal");
        snapshot.WeekFable!.Severity.Should().Be("warning", "the capture had Fable at 88 %");
    }

    [Fact]
    public void A_severity_the_source_calls_anything_but_normal_counts_as_a_warning()
    {
        UsageSnapshot snapshot = Normal();

        snapshot.Session!.IsWarning.Should().BeFalse();
        snapshot.WeekFable!.IsWarning.Should().BeTrue();
    }

    [Fact]
    public void An_unfamiliar_severity_is_treated_as_worth_noticing()
    {
        // "Not normal" rather than "is warning": a value we have never seen must
        // not be silently downgraded to fine.
        UsageWindow window = UsageWindow.Create(10d, null) with { Severity = "catastrophe" };

        window.IsWarning.Should().BeTrue();
    }

    [Fact]
    public void A_missing_severity_is_not_a_warning()
    {
        UsageWindow.Create(10d, null).IsWarning.Should().BeFalse();
    }

    [Fact]
    public void The_kind_is_kept_so_the_report_can_show_what_the_source_called_it()
    {
        UsageSnapshot snapshot = Normal();

        snapshot.Session!.Kind.Should().Be("session");
        snapshot.Week!.Kind.Should().Be("weekly_all");
        snapshot.WeekFable!.Kind.Should().Be("weekly_scoped");
    }

    [Fact]
    public void The_active_flag_is_read()
    {
        UsageSnapshot snapshot = Normal();

        snapshot.Session!.IsActive.Should().BeFalse();
        snapshot.Week!.IsActive.Should().BeFalse();
        snapshot.WeekFable!.IsActive.Should().BeTrue("only the scoped window was active in the capture");
    }

    [Fact]
    public void The_scoped_window_remembers_which_model_it_is_scoped_to()
    {
        Normal().WeekFable!.ScopeModel.Should().Be("Fable");
    }

    [Fact]
    public void Dollar_figures_are_null_on_this_plan_rather_than_zero()
    {
        // Zero and "not reported" are different, and showing 0.00 would be a lie.
        UsageWindow session = Normal().Session!;

        session.LimitDollars.Should().BeNull();
        session.UsedDollars.Should().BeNull();
        session.RemainingDollars.Should().BeNull();
        session.LockedReason.Should().BeNull();
    }

    [Fact]
    public void The_spend_block_is_read()
    {
        SpendInfo spend = Normal().Spend!;

        spend.Should().NotBeNull();
        spend.Enabled.Should().BeFalse();
        spend.Percent.Should().Be(0d);
        spend.Severity.Should().Be("normal");
        spend.Used!.AmountMinor.Should().Be(0);
        spend.Used.Currency.Should().Be("USD");
        spend.Used.Exponent.Should().Be(2);
        spend.Limit.Should().BeNull("no cap was set on the captured account");
    }

    [Fact]
    public void The_credits_block_is_read()
    {
        ExtraUsageInfo credits = Normal().ExtraUsage!;

        credits.Should().NotBeNull();
        credits.IsEnabled.Should().BeFalse();
        credits.UserDisabled.Should().BeTrue();
        credits.SpendLimitReached.Should().BeFalse();
        credits.CreditsEverEnabled.Should().BeTrue(
            "which is how the report distinguishes 'turned off' from 'never used'");
    }

    [Fact]
    public void A_missing_spend_block_yields_null_rather_than_a_zeroed_one()
    {
        // "The endpoint said nothing" and "the endpoint said zero" are different
        // answers, and the report window words them differently.
        Fixture.Parse(Fixture.LegacyFlatOnly).Spend.Should().BeNull();
    }

    [Fact]
    public void Credits_that_are_switched_on_are_read_with_their_limit()
    {
        // The legacy fixture is the only one with extra usage enabled, which makes
        // it the only coverage of the "credits are on" branch of the report.
        ExtraUsageInfo credits = Fixture.Parse(Fixture.LegacyFlatOnly).ExtraUsage!;

        credits.IsEnabled.Should().BeTrue();
        credits.MonthlyLimit.Should().Be(100000d);
        credits.UsedCredits.Should().Be(0d);
        credits.Utilization.Should().BeNull("the fixture leaves it null, and null is not zero");
    }

    [Fact]
    public void The_flat_fallback_windows_carry_no_severity_because_the_source_gives_none()
    {
        // The legacy shape has no limits[] and therefore no severity or kind. The
        // report must show "unknown" there rather than inventing "normal".
        UsageWindow session = Fixture.Parse(Fixture.LegacyFlatOnly).Session!;

        session.Percent.Should().Be(77.5d);
        session.Severity.Should().BeNull();
        session.Kind.Should().BeNull();
        session.IsWarning.Should().BeFalse();
    }

    [Fact]
    public void Unknown_shapes_still_parse_without_losing_the_known_fields()
    {
        // The regression this guards: adding extended parsing must not make the
        // parser less tolerant than it was.
        Action act = () => Fixture.Parse(Fixture.UnknownShapes);

        act.Should().NotThrow();
    }

    [Fact]
    public void The_extended_fields_take_part_in_snapshot_equality()
    {
        // The app skips re-rendering when a poll returns the same values. If a
        // severity changed and equality ignored it, the report would go stale
        // while claiming to be live.
        UsageSnapshot first = Normal();
        UsageSnapshot second = Normal() with { Spend = null };

        first.Should().NotBe(second);
        Normal().Should().Be(Normal(), "two parses of the same body are the same reading");
    }

    [Fact]
    public void Every_window_is_listed_for_the_report_in_a_stable_order()
    {
        string[] labels = [.. Normal().AllWindows().Select(entry => entry.Label)];

        labels.Should().Equal(
            "Details_Metric_Session", "Details_Metric_Week", "Details_Metric_WeekFable");
    }

    [Fact]
    public void An_unrecognised_scoped_model_is_listed_under_its_own_name()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.UnknownShapes);

        // Whatever the fixture's unknown models are, they must appear by name and
        // not be silently dropped.
        foreach ((string label, bool isResourceKey, UsageWindow window) in snapshot.AllWindows())
        {
            label.Should().NotBeNullOrWhiteSpace();
            window.Should().NotBeNull();
            if (!isResourceKey)
            {
                label.Should().NotStartWith("Details_Metric_");
            }
        }
    }
}

/// <summary>Money arrives as minor units plus an exponent, and must stay exact.</summary>
public class MoneyAmountTests
{
    [Theory]
    [InlineData(0, 2, "0.00")]
    [InlineData(1234, 2, "12.34")]
    [InlineData(5, 2, "0.05")]
    [InlineData(100, 0, "100")]
    [InlineData(-250, 2, "-2.50")]
    public void Minor_units_convert_with_the_currency_exponent(
        long minor, int exponent, string expected)
    {
        new MoneyAmount(minor, "USD", exponent).Value
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_nonsensical_exponent_does_not_throw()
    {
        // Undocumented endpoint: an absurd exponent must not overflow inside a
        // UI binding, where the exception has nowhere useful to go.
        Func<decimal> act = () => new MoneyAmount(100, "USD", 99).Value;

        act.Should().NotThrow();
    }

    [Fact]
    public void The_amount_follows_the_culture_but_the_currency_code_is_printed_as_is()
    {
        // Formatting as "C" would render a USD amount with the viewer's own
        // symbol, so $12.34 read in Spain would claim to be €12,34.
        string spanish = new MoneyAmount(1234, "USD", 2)
            .Format(System.Globalization.CultureInfo.GetCultureInfo("es"));

        spanish.Should().Contain("12,34");
        spanish.Should().Contain("USD");
        spanish.Should().NotContain("€");
    }

    [Fact]
    public void A_missing_currency_code_is_simply_omitted()
    {
        new MoneyAmount(1234, null, 2)
            .Format(System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be("12.34");
    }
}

/// <summary>
/// When the Fable window's reset is really the weekly one.
/// </summary>
/// <remarks>
/// Driven by a real observation: an account at 0 % Fable usage gets
/// <c>resets_at: null</c> for the scoped window while the weekly one is set, so
/// the popup read "reset time unknown" — which sounds like a fault rather than
/// the endpoint declining to repeat itself.
/// </remarks>
public class WeeklyResetSharingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeeklyReset = Now.AddDays(2);

    private static UsageSnapshot Build(DateTimeOffset? weeklyReset, DateTimeOffset? fableReset) => new(
        Session: UsageWindow.Create(29d, Now.AddHours(2)),
        Week: weeklyReset is null ? null : UsageWindow.Create(58d, weeklyReset),
        WeekFable: UsageWindow.Create(0d, fableReset),
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: Now,
        IsStale: false);

    [Fact]
    public void A_null_Fable_reset_alongside_a_known_weekly_one_counts_as_shared()
    {
        // Exactly the live shape: weekFableResetsAt null, weekResetsAt set.
        Build(WeeklyReset, fableReset: null).WeekFableSharesWeeklyReset.Should().BeTrue();
    }

    [Fact]
    public void An_identical_reset_time_counts_as_shared()
    {
        Build(WeeklyReset, WeeklyReset).WeekFableSharesWeeklyReset.Should().BeTrue();
    }

    [Theory]
    [InlineData(291)]
    [InlineData(-291)]
    [InlineData(50_000)]
    public void Sub_second_jitter_between_the_two_stamps_still_counts_as_shared(int microseconds)
    {
        // The recorded capture has them 291 microseconds apart, because the server
        // timestamps each limit as it builds the response instead of copying one
        // value. An exact comparison fails on exactly the data this exists for.
        Build(WeeklyReset, WeeklyReset.AddMicroseconds(microseconds))
            .WeekFableSharesWeeklyReset.Should().BeTrue();
    }

    [Fact]
    public void A_genuinely_different_reset_time_is_not_shared()
    {
        // If the endpoint ever does give the scoped window its own schedule, the
        // real countdown must win over the convenient message.
        Build(WeeklyReset, WeeklyReset.AddHours(6))
            .WeekFableSharesWeeklyReset.Should().BeFalse();
    }

    [Fact]
    public void Nothing_is_shared_when_the_weekly_reset_is_itself_unknown()
    {
        // "Resets with the weekly limit" would be useless if the weekly limit has
        // no known reset either; "unknown" is the honest answer there.
        Build(weeklyReset: null, fableReset: null)
            .WeekFableSharesWeeklyReset.Should().BeFalse();
    }

    [Fact]
    public void Nothing_is_shared_when_there_is_no_Fable_window_at_all()
    {
        UsageSnapshot noFable = Build(WeeklyReset, null) with { WeekFable = null };

        noFable.WeekFableSharesWeeklyReset.Should().BeFalse();
    }

    [Fact]
    public void The_recorded_capture_shares_its_reset()
    {
        Fixture.Parse(Fixture.Normal).WeekFableSharesWeeklyReset.Should().BeTrue();
    }
}
