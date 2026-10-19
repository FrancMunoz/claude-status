namespace ClaudeStatus.Core.Tests;

/// <summary>
/// <c>seven_day_breakdown</c> and <c>weekly_scoped_shares</c>, both first seen on
/// 2026-10-17 and asserted against that real capture.
/// </summary>
public class BreakdownParsingTests
{
    private static UsageSnapshot Captured() => Fixture.Parse(Fixture.Breakdown);

    private static UsageSnapshot ParseBody(string json) => UsageResponseParser.Parse(json, Fixture.FixedNow);

    [Fact]
    public void The_breakdown_rows_are_read_in_the_sources_order()
    {
        UsageBreakdown breakdown = Captured().WeekBreakdown!;

        breakdown.Rows.Select(row => row.Key).Should().Equal("claude_code", "chat", "cowork", "other");
        breakdown.Rows.Select(row => row.Percent).Should().Equal(21d, 3d, 76d, 0d);
        breakdown.Rows[0].DisplayName.Should().Be("Claude Code");
    }

    [Fact]
    public void The_breakdown_keeps_its_own_timestamps()
    {
        UsageBreakdown breakdown = Captured().WeekBreakdown!;

        breakdown.AsOf.Should().Be(new DateTimeOffset(2026, 10, 17, 11, 0, 43, TimeSpan.Zero).AddTicks(2360670));
        breakdown.WindowStartedAt.Should().NotBeNull();
        breakdown.WindowStartedAt!.Value.UtcDateTime.Should().Be(new DateTime(2026, 10, 11, 23, 0, 0, DateTimeKind.Utc).AddTicks(1944670));
    }

    [Fact]
    public void The_scoped_share_lands_on_the_window_its_index_points_at()
    {
        UsageSnapshot snapshot = Captured();

        snapshot.WeekFable!.WeeklySharePercent.Should().Be(27d);
        snapshot.WeekFable.WeeklyAllowancePercent.Should().Be(50d);
        snapshot.Week!.WeeklySharePercent.Should().BeNull("only limits[2] had a share");
        snapshot.Session!.WeeklyAllowancePercent.Should().BeNull();
    }

    [Fact]
    public void The_share_is_the_scoped_percentage_scaled_by_the_allowance()
    {
        // This is what pins down what the two undocumented numbers mean.
        UsageWindow fable = Captured().WeekFable!;

        (fable.Percent * fable.WeeklyAllowancePercent!.Value / 100d)
            .Should().BeApproximately(fable.WeeklySharePercent!.Value, 1d);
    }

    [Fact]
    public void Older_responses_have_neither_block()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        snapshot.WeekBreakdown.Should().BeNull();
        snapshot.WeekFable!.WeeklySharePercent.Should().BeNull();
    }

    [Fact]
    public void A_share_index_counts_skipped_entries_too()
    {
        // limits[0] is unreadable, so the scoped window is still index 1 in the raw array.
        UsageSnapshot snapshot = ParseBody("""
            {
              "limits": [
                "garbage",
                { "kind": "weekly_scoped", "percent": 40,
                  "scope": { "model": { "display_name": "Fable" } } }
              ],
              "weekly_scoped_shares": [
                { "limit_index": 1, "used_percent_of_weekly": 20, "allowance_percent_of_weekly": 50 }
              ]
            }
            """);

        snapshot.WeekFable!.WeeklySharePercent.Should().Be(20d);
    }

    [Theory]
    [InlineData("""{ "used_percent_of_weekly": 20 }""")]
    [InlineData("""{ "limit_index": -1, "used_percent_of_weekly": 20 }""")]
    [InlineData("""{ "limit_index": 0.5, "used_percent_of_weekly": 20 }""")]
    [InlineData("""{ "limit_index": 9, "used_percent_of_weekly": 20 }""")]
    [InlineData("""{ "limit_index": "zero", "used_percent_of_weekly": 20 }""")]
    [InlineData("null")]
    public void A_share_that_points_nowhere_is_ignored(string entry)
    {
        UsageSnapshot snapshot = ParseBody($$"""
            {
              "limits": [ { "kind": "session", "percent": 10 } ],
              "weekly_scoped_shares": [ {{entry}} ]
            }
            """);

        snapshot.Session!.Percent.Should().Be(10d);
        snapshot.Session.WeeklySharePercent.Should().BeNull();
    }

    [Fact]
    public void Breakdown_rows_without_a_key_or_a_number_are_dropped_and_percents_clamped()
    {
        UsageBreakdown breakdown = ParseBody("""
            {
              "seven_day_breakdown": {
                "as_of": null,
                "rows": [
                  { "key": "claude_code", "percent": 140 },
                  { "key": "", "percent": 5 },
                  { "display_name": "No key", "percent": 5 },
                  { "key": "chat", "percent": null },
                  { "key": "brand_new_surface", "display_name": "Something new", "percent": "7.5" },
                  42
                ]
              }
            }
            """).WeekBreakdown!;

        breakdown.AsOf.Should().BeNull();
        breakdown.Rows.Should().Equal(
            new UsageBreakdownRow("claude_code", null, 100d),
            new UsageBreakdownRow("brand_new_surface", "Something new", 7.5d));
    }

    [Theory]
    [InlineData("""{ "seven_day_breakdown": null }""")]
    [InlineData("""{ "seven_day_breakdown": [] }""")]
    [InlineData("""{ "weekly_scoped_shares": {} }""")]
    public void A_block_of_the_wrong_shape_does_not_break_the_parse(string json)
    {
        UsageSnapshot snapshot = ParseBody(json);

        snapshot.WeekBreakdown.Should().BeNull();
    }

    [Fact]
    public void A_breakdown_with_a_rows_field_of_the_wrong_shape_is_kept_empty()
    {
        ParseBody("""{ "seven_day_breakdown": { "rows": {} } }""")
            .WeekBreakdown!.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Two_parses_of_the_same_body_are_equal()
    {
        // The monitor skips re-rendering on an unchanged reading; a list compared
        // by reference would defeat that on every poll.
        UsageSnapshot first = Captured();
        UsageSnapshot second = Captured();

        second.Should().Be(first);
        second.GetHashCode().Should().Be(first.GetHashCode());
    }

    [Fact]
    public void A_changed_breakdown_row_makes_the_snapshot_differ()
    {
        UsageSnapshot first = Captured();
        UsageBreakdown changed = first.WeekBreakdown! with
        {
            Rows = [.. first.WeekBreakdown.Rows.Take(3), new UsageBreakdownRow("other", "Other", 1d)],
        };

        (first with { WeekBreakdown = changed }).Should().NotBe(first);
    }
}
