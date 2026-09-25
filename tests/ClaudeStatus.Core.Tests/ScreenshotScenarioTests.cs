namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The scripted day the macOS and Windows screenshots both pose for.
/// </summary>
/// <remarks>
/// The point of the scenario is that two machines photographed minutes apart
/// agree, so what is worth pinning is the readings and the countdown's slack -
/// the one thing that would otherwise tick over mid-shoot. See
/// <c>docs/screenshots.md</c>.
/// </remarks>
public class ScreenshotScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    private static async Task<UsageSnapshot> Fetch(FakeUsageScenario scenario)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);
        return await new FakeUsageProvider(scenario, clock).FetchAsync(default);
    }

    [Fact]
    public async Task It_shows_the_same_readings_as_the_healthy_day()
    {
        // The numbers the README's images have always carried. A scenario that
        // also changed them would make every existing screenshot the odd one out.
        UsageSnapshot shot = await Fetch(FakeUsageScenario.Screenshot);
        UsageSnapshot healthy = await Fetch(FakeUsageScenario.Healthy);

        shot.Session!.Percent.Should().Be(healthy.Session!.Percent).And.Be(29d);
        shot.Week!.Percent.Should().Be(healthy.Week!.Percent).And.Be(58d);
        shot.WeekFable!.Percent.Should().Be(healthy.WeekFable!.Percent).And.Be(44d);
    }

    /// <summary>The countdown a scenario shows <paramref name="second"/> seconds after its fetch.</summary>
    private static async Task<string> Countdown(FakeUsageScenario scenario, int second)
    {
        UsageSnapshot shot = await Fetch(scenario);
        return IndicatorText.FormatCountdown(shot.Session!.TimeUntilReset(Now.AddSeconds(second)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(49)]
    public async Task The_countdown_holds_its_reading_across_a_whole_poll(int second)
    {
        // The failure this exists to stop: one machine photographed early in its
        // poll cycle reading 2:37 and the other, moments later in its own, 2:36.
        // The default poll comes round at 60 s and puts it back.
        (await Countdown(FakeUsageScenario.Screenshot, second)).Should().Be("(2:37)");
    }

    [Fact]
    public async Task The_healthy_day_is_the_one_that_turns_over_at_once()
    {
        // Why the scenario is needed at all. Healthy resets on the whole minute,
        // so the countdown drops a minute one second after the fetch and two
        // shots taken seconds apart disagree.
        (await Countdown(FakeUsageScenario.Healthy, 1)).Should().Be("(2:36)");
        (await Countdown(FakeUsageScenario.Screenshot, 1)).Should().Be("(2:37)");
    }

    [Fact]
    public async Task The_slack_runs_out_rather_than_lasting_forever()
    {
        // Honest about its limit: this buys most of a poll, not a frozen clock.
        // A machine left unpolled long enough still slides, which is what the
        // "refresh, then shoot" line in docs/screenshots.md is for.
        (await Countdown(FakeUsageScenario.Screenshot, 51)).Should().Be("(2:36)");
    }

    [Fact]
    public async Task Nothing_in_it_is_near_the_threshold()
    {
        // A red indicator is a different picture. The scripted day has to be calm.
        UsageSnapshot shot = await Fetch(FakeUsageScenario.Screenshot);

        shot.Session!.Percent.Should().BeLessThan(ThresholdEvaluator.DefaultThresholdPercent);
        shot.Week!.Percent.Should().BeLessThan(ThresholdEvaluator.DefaultThresholdPercent);
        shot.WeekFable!.Percent.Should().BeLessThan(ThresholdEvaluator.DefaultThresholdPercent);
        shot.IsStale.Should().BeFalse();
    }
}
