namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The parser is the one place a schema change can silently corrupt what the
/// user sees, so these tests lean hard on the recorded fixtures and on hostile
/// input rather than on hand-written happy paths.
/// </summary>
public class UsageResponseParserTests
{
    [Fact]
    public void Parses_the_real_recorded_response()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        snapshot.Session!.Percent.Should().Be(29d);
        snapshot.Week!.Percent.Should().Be(58d);
        snapshot.WeekFable!.Percent.Should().Be(88d);
        snapshot.IsStale.Should().BeFalse();
        snapshot.FetchedAt.Should().Be(Fixture.FixedNow);
    }

    [Fact]
    public void Reads_reset_times_from_the_real_response()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        snapshot.Session!.ResetsAt.Should().Be(
            new DateTimeOffset(2026, 9, 4, 15, 39, 59, 968, TimeSpan.Zero).AddTicks(2460));
        snapshot.Week!.ResetsAt!.Value.Should().BeAfter(snapshot.Session.ResetsAt!.Value);
    }

    [Fact]
    public void Prefers_the_limits_array_over_the_legacy_flat_keys()
    {
        // Both sources are present in the real response and agree; the point is
        // that limits[] is what we read, so a future divergence follows limits[].
        const string json = """
            {
              "five_hour": { "utilization": 11.0, "resets_at": "2030-01-01T00:00:00+00:00" },
              "limits": [
                { "kind": "session", "percent": 77, "resets_at": "2030-06-01T00:00:00+00:00" }
              ]
            }
            """;

        UsageSnapshot snapshot = UsageResponseParser.Parse(json, Fixture.FixedNow);

        snapshot.Session!.Percent.Should().Be(77d);
    }

    [Fact]
    public void Falls_back_to_flat_keys_when_there_is_no_limits_array()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.LegacyFlatOnly);

        snapshot.Session!.Percent.Should().Be(77.5d);
        snapshot.Week!.Percent.Should().Be(91d);
        snapshot.WeekFable.Should().BeNull("this response has no scoped window at all");
    }

    [Fact]
    public void Returns_null_for_a_missing_Fable_window_rather_than_zero()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.NoScopedWindow);

        snapshot.WeekFable.Should().BeNull();
        snapshot.Session!.Percent.Should().Be(12d);
        snapshot.Week!.Percent.Should().Be(40d);
    }

    [Fact]
    public void Survives_unknown_kinds_models_and_top_level_keys()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.UnknownShapes);

        snapshot.Session!.Percent.Should().Be(5.5d, "a float percent must parse");
        snapshot.WeekFable!.Percent.Should().Be(61.25d);
        snapshot.Week!.Percent.Should().Be(0d, "the weekly_all entry is present with percent 0");
    }

    [Fact]
    public void Keeps_unknown_scoped_models_instead_of_dropping_them()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.UnknownShapes);

        snapshot.OtherWindows.Should().ContainKey("SomeUnreleasedModel");
        snapshot.OtherWindows["SomeUnreleasedModel"].Percent.Should().Be(10d);
    }

    [Fact]
    public void Accepts_a_null_reset_time()
    {
        const string json = """
            { "limits": [ { "kind": "session", "percent": 5, "resets_at": null } ] }
            """;

        UsageSnapshot snapshot = UsageResponseParser.Parse(json, Fixture.FixedNow);

        snapshot.Session!.ResetsAt.Should().BeNull();
        snapshot.Session.TimeUntilReset(Fixture.FixedNow).Should().BeNull();
    }

    [Theory]
    [InlineData("29", 29d)]
    [InlineData("29.0", 29d)]
    [InlineData("29.75", 29.75d)]
    [InlineData("\"29.5\"", 29.5d)]
    public void Accepts_a_percent_as_int_float_or_numeric_string(string literal, double expected)
    {
        string json = $$"""{ "limits": [ { "kind": "session", "percent": {{literal}} } ] }""";

        UsageResponseParser.Parse(json, Fixture.FixedNow).Session!.Percent.Should().Be(expected);
    }

    [Theory]
    [InlineData(-5d, 0d)]
    [InlineData(150d, 100d)]
    public void Clamps_a_percentage_outside_zero_to_one_hundred(double raw, double expected)
    {
        string json = $$"""{ "limits": [ { "kind": "session", "percent": {{raw.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } ] }""";

        UsageResponseParser.Parse(json, Fixture.FixedNow).Session!.Percent.Should().Be(expected);
    }

    [Fact]
    public void Ignores_a_limits_entry_that_is_not_an_object()
    {
        const string json = """
            { "limits": [ "nonsense", 42, null, { "kind": "session", "percent": 3 } ] }
            """;

        UsageResponseParser.Parse(json, Fixture.FixedNow).Session!.Percent.Should().Be(3d);
    }

    [Fact]
    public void Ignores_a_limits_entry_with_no_percent()
    {
        const string json = """
            { "limits": [ { "kind": "session", "resets_at": "2030-01-01T00:00:00+00:00" } ] }
            """;

        UsageResponseParser.Parse(json, Fixture.FixedNow).Session.Should().BeNull();
    }

    [Fact]
    public void Returns_an_all_null_snapshot_for_an_empty_object_rather_than_throwing()
    {
        UsageSnapshot snapshot = UsageResponseParser.Parse("{}", Fixture.FixedNow);

        snapshot.Session.Should().BeNull();
        snapshot.Week.Should().BeNull();
        snapshot.WeekFable.Should().BeNull();
        snapshot.OtherWindows.Should().BeEmpty();
    }

    [Fact]
    public void Ignores_a_top_level_bucket_that_is_null()
    {
        // nimbus_quill was an object while its siblings were null in the real
        // capture, so either shape must be survivable.
        const string json = """{ "five_hour": null, "seven_day": null, "nimbus_quill": { "utilization": 0.0 } }""";

        UsageSnapshot snapshot = UsageResponseParser.Parse(json, Fixture.FixedNow);

        snapshot.Session.Should().BeNull();
        snapshot.Week.Should().BeNull();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"unclosed\": ")]
    public void Throws_UsageParseException_for_a_body_that_is_not_json(string body)
    {
        Action act = () => UsageResponseParser.Parse(body, Fixture.FixedNow);

        act.Should().Throw<UsageParseException>();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void Throws_UsageParseException_when_the_root_is_not_an_object(string body)
    {
        Action act = () => UsageResponseParser.Parse(body, Fixture.FixedNow);

        act.Should().Throw<UsageParseException>();
    }

    [Fact]
    public void Never_puts_the_response_body_in_the_exception_message()
    {
        // A failing response can be an error page echoing request headers, which
        // would put the bearer token into a log. See docs/security.md T2.
        const string body = "Authorization: Bearer sk-ant-oat01-not-a-real-token-abcdefgh";

        Action act = () => UsageResponseParser.Parse(body, Fixture.FixedNow);

        act.Should().Throw<UsageParseException>()
            .Which.Message.Should().NotContain("sk-ant-").And.NotContain("Bearer");
    }

    [Fact]
    public void Throws_ArgumentNullException_for_a_null_body()
    {
        Action act = () => UsageResponseParser.Parse(null!, Fixture.FixedNow);

        act.Should().Throw<ArgumentNullException>();
    }
}
