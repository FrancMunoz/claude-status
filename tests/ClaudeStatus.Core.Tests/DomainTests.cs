namespace ClaudeStatus.Core.Tests;

public class UsageWindowTests
{
    [Theory]
    [InlineData(-1d, 0d)]
    [InlineData(0d, 0d)]
    [InlineData(50.5d, 50.5d)]
    [InlineData(100d, 100d)]
    [InlineData(101d, 100d)]
    public void Create_clamps_into_zero_to_one_hundred(double raw, double expected)
    {
        UsageWindow.Create(raw, null).Percent.Should().Be(expected);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void Create_treats_a_non_finite_percentage_as_zero(double raw)
    {
        UsageWindow.Create(raw, null).Percent.Should().Be(0d);
    }

    [Fact]
    public void TimeUntilReset_counts_down()
    {
        var window = UsageWindow.Create(10d, Fixture.FixedNow.AddMinutes(90));

        window.TimeUntilReset(Fixture.FixedNow).Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void TimeUntilReset_is_zero_once_the_reset_has_passed()
    {
        // The source has simply not caught up yet; a negative countdown would
        // render as nonsense in the details window.
        var window = UsageWindow.Create(10d, Fixture.FixedNow.AddMinutes(-5));

        window.TimeUntilReset(Fixture.FixedNow).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TimeUntilReset_is_null_when_the_source_gave_no_reset_time()
    {
        UsageWindow.Create(10d, null).TimeUntilReset(Fixture.FixedNow).Should().BeNull();
    }
}

public class UsageSnapshotTests
{
    [Fact]
    public void Empty_starts_out_stale_and_empty()
    {
        UsageSnapshot empty = UsageSnapshot.Empty(Fixture.FixedNow);

        empty.IsStale.Should().BeTrue();
        empty.Session.Should().BeNull();
        empty.Week.Should().BeNull();
        empty.WeekFable.Should().BeNull();
        empty.OtherWindows.Should().BeEmpty();
    }

    [Fact]
    public void AsStale_keeps_the_values_and_only_flips_the_flag()
    {
        UsageSnapshot fresh = Fixture.Parse(Fixture.Normal);
        UsageSnapshot stale = fresh.AsStale();

        stale.IsStale.Should().BeTrue();
        stale.Session.Should().Be(fresh.Session);
        stale.Week.Should().Be(fresh.Week);
        stale.FetchedAt.Should().Be(fresh.FetchedAt);
    }

    [Fact]
    public void AsStale_on_an_already_stale_snapshot_returns_the_same_instance()
    {
        UsageSnapshot stale = Fixture.Parse(Fixture.Normal).AsStale();

        stale.AsStale().Should().BeSameAs(stale);
    }

    [Fact]
    public void Age_measures_from_when_it_was_fetched()
    {
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        snapshot.Age(Fixture.FixedNow.AddMinutes(7)).Should().Be(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public void Age_never_goes_negative_if_the_clock_steps_backwards()
    {
        // NTP corrections and laptop sleep both do this.
        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        snapshot.Age(Fixture.FixedNow.AddMinutes(-5)).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ForMode_returns_null_for_an_undefined_mode()
    {
        Fixture.Parse(Fixture.Normal).ForMode((IndicatorMode)99).Should().BeNull();
    }
}

public class FakeUsageProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Is_deterministic_for_a_fixed_clock()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Fixture.FixedNow);
        var provider = new FakeUsageProvider(FakeUsageScenario.Healthy, clock);

        UsageSnapshot first = await provider.FetchAsync(Ct);
        UsageSnapshot second = await provider.FetchAsync(Ct);

        second.Should().Be(first, "screenshots and tests must be reproducible");
    }

    [Theory]
    [InlineData(FakeUsageScenario.Healthy, 29d)]
    [InlineData(FakeUsageScenario.SessionNearLimit, 91d)]
    public async Task Plays_the_requested_scenario(FakeUsageScenario scenario, double expectedSession)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Fixture.FixedNow);

        UsageSnapshot snapshot = await new FakeUsageProvider(scenario, clock).FetchAsync(Ct);

        snapshot.Session!.Percent.Should().Be(expectedSession);
    }

    [Fact]
    public async Task NoFableWindow_omits_the_scoped_window()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Fixture.FixedNow);

        UsageSnapshot snapshot = await new FakeUsageProvider(FakeUsageScenario.NoFableWindow, clock).FetchAsync(Ct);

        snapshot.WeekFable.Should().BeNull();
    }

    [Theory]
    [InlineData(FakeUsageScenario.AlwaysFails, UsageFetchFailure.Network)]
    [InlineData(FakeUsageScenario.RateLimited, UsageFetchFailure.RateLimited)]
    [InlineData(FakeUsageScenario.Unauthorized, UsageFetchFailure.Unauthorized)]
    public async Task Failure_scenarios_report_the_right_failure(
        FakeUsageScenario scenario, UsageFetchFailure expected)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Fixture.FixedNow);

        Func<Task> act = () => new FakeUsageProvider(scenario, clock).FetchAsync(Ct);

        (await act.Should().ThrowAsync<UsageFetchException>()).Which.Failure.Should().Be(expected);
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => new FakeUsageProvider().FetchAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public class AccessTokenLeaseTests
{
    [Fact]
    public void Zeroes_the_buffer_on_dispose()
    {
        byte[] buffer = "sk-ant-oat01-AAAA"u8.ToArray();
        var lease = AccessTokenLease.Own(buffer);

        lease.HasToken.Should().BeTrue();
        lease.Dispose();

        buffer.Should().AllSatisfy(b => b.Should().Be(0), "the token must not survive the lease");
    }

    [Fact]
    public void Reports_an_absent_token_without_throwing()
    {
        using var lease = AccessTokenLease.Own(null);

        lease.HasToken.Should().BeFalse();
        lease.Token.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void An_empty_buffer_counts_as_no_token()
    {
        using var lease = AccessTokenLease.Own([]);

        lease.HasToken.Should().BeFalse();
    }

    [Fact]
    public void ToString_never_reveals_the_token()
    {
        using var lease = AccessTokenLease.Own("sk-ant-oat01-SECRET"u8.ToArray());

        lease.ToString().Should().NotContain("sk-ant-").And.Contain("present");
    }

    [Fact]
    public async Task AcquireAsync_takes_the_token_from_the_source()
    {
        using AccessTokenLease lease = await AccessTokenLease.AcquireAsync(
            new StubTokenSource("sk-ant-oat01-AAAA"), TestContext.Current.CancellationToken);

        lease.HasToken.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_reports_no_token_when_the_user_is_not_logged_in()
    {
        using AccessTokenLease lease = await AccessTokenLease.AcquireAsync(
            new StubTokenSource(null), TestContext.Current.CancellationToken);

        lease.HasToken.Should().BeFalse();
    }

    private sealed class StubTokenSource(string? token) : IAccessTokenSource
    {
        public string DescriptionKey => "stub";

        public Task<byte[]?> GetAccessTokenAsync(CancellationToken ct)
            => Task.FromResult(token is null ? null : System.Text.Encoding.UTF8.GetBytes(token));
    }
}

public class PollingOptionsTests
{
    [Fact]
    public void Normalized_enforces_the_sixty_second_floor()
    {
        var options = new PollingOptions { BaseInterval = TimeSpan.FromSeconds(1) }.Normalized();

        options.BaseInterval.Should().Be(PollingOptions.MinimumInterval);
    }

    [Fact]
    public void Normalized_keeps_the_ceiling_at_or_above_the_base()
    {
        var options = new PollingOptions
        {
            BaseInterval = TimeSpan.FromSeconds(120),
            MaxInterval = TimeSpan.FromSeconds(30),
        }.Normalized();

        options.MaxInterval.Should().BeGreaterThanOrEqualTo(options.BaseInterval);
    }

    [Theory]
    [InlineData(-1d, 0d)]
    [InlineData(2d, 1d)]
    [InlineData(double.NaN, 0d)]
    public void Normalized_clamps_the_jitter_fraction(double raw, double expected)
    {
        new PollingOptions { JitterFraction = raw }.Normalized().JitterFraction.Should().Be(expected);
    }

    [Fact]
    public void Normalized_rejects_a_negative_cooldown()
    {
        new PollingOptions { ForcedRefreshCooldown = TimeSpan.FromSeconds(-5) }
            .Normalized().ForcedRefreshCooldown.Should().Be(TimeSpan.Zero);
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Defaults_store_no_credential_and_use_the_Claude_Code_login()
    {
        var settings = new AppSettings();

        settings.CredentialSource.Should().Be(CredentialSource.ClaudeCodeLogin);
        settings.HasCredential.Should().BeFalse();
        settings.UseFakeProvider.Should().BeFalse();
    }

    [Fact]
    public void Normalized_replaces_an_out_of_range_enum_with_the_default()
    {
        var settings = new AppSettings { IndicatorMode = (IndicatorMode)77 }.Normalized();

        settings.IndicatorMode.Should().Be(IndicatorMode.SessionPercent);
    }

    [Fact]
    public void Normalized_tolerates_a_null_polling_block_from_a_hand_edited_file()
    {
        var settings = new AppSettings { Polling = null! }.Normalized();

        settings.Polling.Should().NotBeNull();
        settings.Polling.BaseInterval.Should().Be(PollingOptions.MinimumInterval);
    }
}

public class UsageSnapshotEqualityTests
{
    private static UsageSnapshot Make(params (string Model, double Percent)[] others) => new(
        Session: UsageWindow.Create(29d, Fixture.FixedNow.AddHours(1)),
        Week: UsageWindow.Create(58d, Fixture.FixedNow.AddDays(2)),
        WeekFable: UsageWindow.Create(88d, Fixture.FixedNow.AddDays(2)),
        OtherWindows: others.ToDictionary(o => o.Model, o => UsageWindow.Create(o.Percent, null)),
        FetchedAt: Fixture.FixedNow,
        IsStale: false);

    [Fact]
    public void Two_structurally_identical_snapshots_are_equal()
    {
        // The tray icon is only re-rendered when a poll produces something new,
        // so this has to be value equality, not reference equality.
        Make(("Opus", 10d)).Should().Be(Make(("Opus", 10d)));
    }

    [Fact]
    public void Identical_snapshots_share_a_hash_code()
    {
        Make(("Opus", 10d)).GetHashCode().Should().Be(Make(("Opus", 10d)).GetHashCode());
    }

    [Fact]
    public void Snapshots_differing_only_in_OtherWindows_are_not_equal()
    {
        Make(("Opus", 10d)).Should().NotBe(Make(("Opus", 11d)));
        Make(("Opus", 10d)).Should().NotBe(Make(("Sonnet", 10d)));
        Make(("Opus", 10d)).Should().NotBe(Make());
    }

    [Fact]
    public void A_stale_snapshot_is_not_equal_to_its_fresh_original()
    {
        UsageSnapshot fresh = Make();

        fresh.Should().NotBe(fresh.AsStale());
    }

    [Fact]
    public void A_snapshot_is_never_equal_to_null()
    {
        Make().Equals(null).Should().BeFalse();
    }
}
