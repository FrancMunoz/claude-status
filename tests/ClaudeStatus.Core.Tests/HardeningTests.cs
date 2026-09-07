using ClaudeStatus.Platform;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.Core.Tests;

public class SnapshotCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-cache", Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Now = new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonSnapshotCache Cache(DateTimeOffset? now = null)
        => new(_directory, new FakeTimeProvider(now ?? Now));

    private static UsageSnapshot Sample(DateTimeOffset fetchedAt) => new(
        Session: UsageWindow.Create(29d, fetchedAt.AddHours(2)),
        Week: UsageWindow.Create(58d, fetchedAt.AddDays(2)),
        WeekFable: UsageWindow.Create(88d, fetchedAt.AddDays(2)),
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: fetchedAt,
        IsStale: false);

    [Fact]
    public async Task Returns_null_when_there_is_no_cache_yet()
    {
        (await Cache().LoadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Round_trips_a_reading()
    {
        JsonSnapshotCache cache = Cache();
        await cache.SaveAsync(Sample(Now), Ct);

        UsageSnapshot? loaded = await cache.LoadAsync(Ct);

        loaded.Should().NotBeNull();
        loaded!.Session!.Percent.Should().Be(29d);
        loaded.Week!.Percent.Should().Be(58d);
        loaded.WeekFable!.Percent.Should().Be(88d);
        loaded.FetchedAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_loaded_reading_is_always_stale()
    {
        // By definition it was fetched before this process existed, so showing it
        // as fresh would be a lie.
        JsonSnapshotCache cache = Cache();
        await cache.SaveAsync(Sample(Now), Ct);

        (await cache.LoadAsync(Ct))!.IsStale.Should().BeTrue();
    }

    [Fact]
    public async Task Preserves_reset_times_so_countdowns_survive_a_restart()
    {
        JsonSnapshotCache cache = Cache();
        UsageSnapshot original = Sample(Now);
        await cache.SaveAsync(original, Ct);

        UsageSnapshot loaded = (await cache.LoadAsync(Ct))!;

        loaded.Session!.ResetsAt.Should().Be(original.Session!.ResetsAt);
    }

    [Fact]
    public async Task Ignores_a_reading_older_than_the_staleness_cap()
    {
        // A two-day-old percentage is not information, it is a misleading number.
        JsonSnapshotCache writer = Cache();
        await writer.SaveAsync(Sample(Now), Ct);

        JsonSnapshotCache reader = Cache(Now + JsonSnapshotCache.MaximumAge + TimeSpan.FromHours(1));

        (await reader.LoadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Keeps_a_reading_inside_the_staleness_cap()
    {
        JsonSnapshotCache writer = Cache();
        await writer.SaveAsync(Sample(Now), Ct);

        JsonSnapshotCache reader = Cache(Now + JsonSnapshotCache.MaximumAge - TimeSpan.FromHours(1));

        (await reader.LoadAsync(Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_corrupt_cache_is_treated_as_no_cache_rather_than_a_crash()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, JsonSnapshotCache.FileName), "{ not json", Ct);

        (await Cache().LoadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Handles_a_reading_with_no_Fable_window()
    {
        JsonSnapshotCache cache = Cache();
        await cache.SaveAsync(Sample(Now) with { WeekFable = null }, Ct);

        UsageSnapshot loaded = (await cache.LoadAsync(Ct))!;

        loaded.WeekFable.Should().BeNull();
        loaded.Session.Should().NotBeNull();
    }

    [Fact]
    public async Task The_cache_file_contains_no_credential_shaped_text()
    {
        JsonSnapshotCache cache = Cache();
        await cache.SaveAsync(Sample(Now), Ct);

        string json = await File.ReadAllTextAsync(cache.CacheFilePath, Ct);

        json.Should().NotContain("sk-ant-");
        Redactor.LooksRedacted(json).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_blank_config_directory()
    {
        FluentActions.Invoking(() => new JsonSnapshotCache(" "))
            .Should().Throw<ArgumentException>();
    }
}

public class OfflineStartTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_seeded_monitor_shows_cached_values_even_when_every_fetch_fails()
    {
        // The offline-start case: no network, but the tray must not be blank.
        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.AlwaysFails, clock),
            new PollingOptions { JitterFraction = 0d },
            clock,
            () => 0d);

        monitor.SeedFrom(Fixture.Parse(Fixture.Normal));
        await monitor.RefreshNowAsync(Ct);

        monitor.Latest.Should().NotBeNull();
        monitor.Latest!.Session!.Percent.Should().Be(29d);
        monitor.Latest.IsStale.Should().BeTrue();
        monitor.Status.Failure.Should().Be(UsageFetchFailure.Network);
    }

    [Fact]
    public void Seeding_publishes_immediately_so_the_tray_paints_at_once()
    {
        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        List<UsageSnapshot> received = [];
        using IDisposable subscription = monitor.Snapshots.Subscribe(new Collector(received));

        monitor.SeedFrom(Fixture.Parse(Fixture.Normal));

        received.Should().ContainSingle();
        received[0].IsStale.Should().BeTrue();
    }

    [Fact]
    public async Task A_successful_fetch_replaces_the_seeded_reading()
    {
        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.SessionNearLimit, clock),
            new PollingOptions(),
            clock);

        monitor.SeedFrom(Fixture.Parse(Fixture.Normal));
        await monitor.RefreshNowAsync(Ct);

        monitor.Latest!.Session!.Percent.Should().Be(91d);
        monitor.Latest.IsStale.Should().BeFalse();
    }

    private sealed class Collector(List<UsageSnapshot> target) : IObserver<UsageSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(UsageSnapshot value) => target.Add(value);
    }
}

public class SingleInstanceGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-instance", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void The_first_instance_wins()
    {
        using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(_directory);

        guard.IsPrimaryInstance.Should().BeTrue();
        guard.ShouldRun.Should().BeTrue();
    }

    [Fact]
    public void A_second_instance_loses()
    {
        // Autostart plus a manual launch is the real scenario, and two tray icons
        // is a visible bug.
        using SingleInstanceGuard first = SingleInstanceGuard.Acquire(_directory);
        using SingleInstanceGuard second = SingleInstanceGuard.Acquire(_directory);

        first.IsPrimaryInstance.Should().BeTrue();
        second.IsPrimaryInstance.Should().BeFalse();
        second.ShouldRun.Should().BeFalse();
    }

    [Fact]
    public void The_lock_is_released_when_the_first_instance_exits()
    {
        SingleInstanceGuard first = SingleInstanceGuard.Acquire(_directory);
        first.IsPrimaryInstance.Should().BeTrue();
        first.Dispose();

        using SingleInstanceGuard second = SingleInstanceGuard.Acquire(_directory);

        second.IsPrimaryInstance.Should().BeTrue("the lock must not outlive the process that held it");
    }

    [Fact]
    public void Different_users_get_different_locks()
    {
        // Scoped by config directory, which is per user.
        string otherDirectory = Path.Combine(
            Path.GetTempPath(), "claudestatus-instance", Guid.NewGuid().ToString("N"));
        try
        {
            using SingleInstanceGuard mine = SingleInstanceGuard.Acquire(_directory);
            using SingleInstanceGuard theirs = SingleInstanceGuard.Acquire(otherDirectory);

            mine.IsPrimaryInstance.Should().BeTrue();
            theirs.IsPrimaryInstance.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(otherDirectory))
            {
                Directory.Delete(otherDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        SingleInstanceGuard guard = SingleInstanceGuard.Acquire(_directory);
        guard.Dispose();

        Action act = guard.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void Creates_the_config_directory_if_it_is_missing()
    {
        using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(_directory);

        Directory.Exists(_directory).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_blank_config_directory()
    {
        FluentActions.Invoking(() => SingleInstanceGuard.Acquire("  "))
            .Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// Guards the source-generated serializer contract.
/// </summary>
/// <remarks>
/// The failure these prevent is nasty: reflection-based serialization works
/// perfectly in a normal build and is silently removed by trimming and AOT, so a
/// missing registration ships as "config quietly stops saving" rather than as a
/// build error. These assert the generated metadata exists and behaves.
/// </remarks>
public class JsonSourceGenerationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-sourcegen", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_survive_a_round_trip_through_the_generated_metadata()
    {
        var store = new JsonConfigStore(_directory);
        var saved = new AppSettings
        {
            IndicatorMode = IndicatorMode.WeekFablePercent,
            ThresholdPercent = 65d,
            CredentialSource = CredentialSource.ManualToken,
            HasCredential = true,
            StartWithOperatingSystem = true,
        };

        await store.SaveAsync(saved, Ct);
        AppSettings loaded = await store.LoadAsync(Ct);

        loaded.IndicatorMode.Should().Be(IndicatorMode.WeekFablePercent);
        loaded.ThresholdPercent.Should().Be(65d);
        loaded.CredentialSource.Should().Be(CredentialSource.ManualToken);
        loaded.HasCredential.Should().BeTrue();
        loaded.StartWithOperatingSystem.Should().BeTrue();
    }

    [Fact]
    public async Task Enums_are_still_written_as_names_under_source_generation()
    {
        // UseStringEnumConverter on the context replaces the non-generic
        // JsonStringEnumConverter, which needs runtime codegen (IL3050).
        var store = new JsonConfigStore(_directory);
        await store.SaveAsync(new AppSettings { IndicatorMode = IndicatorMode.WeekPercent }, Ct);

        string json = await File.ReadAllTextAsync(store.SettingsFilePath, Ct);

        json.Should().Contain("WeekPercent");
        json.Should().NotContain("\"indicatorMode\": 1");
    }

    [Fact]
    public async Task Property_names_are_still_camel_case()
    {
        var store = new JsonConfigStore(_directory);
        await store.SaveAsync(new AppSettings(), Ct);

        string json = await File.ReadAllTextAsync(store.SettingsFilePath, Ct);

        json.Should().Contain("thresholdPercent");
        json.Should().NotContain("ThresholdPercent");
    }

    [Fact]
    public async Task The_nested_polling_block_round_trips()
    {
        // A nested type the generator also has to reach.
        var store = new JsonConfigStore(_directory);
        var saved = new AppSettings
        {
            Polling = new PollingOptions { BaseInterval = TimeSpan.FromSeconds(180) },
        };

        await store.SaveAsync(saved, Ct);

        (await store.LoadAsync(Ct)).Polling.BaseInterval.Should().Be(TimeSpan.FromSeconds(180));
    }

    [Fact]
    public async Task A_failed_save_leaves_no_temp_file_behind()
    {
        // An earlier version left 0-byte .tmp files after a failure, which is how
        // the trimmed build's broken cache announced itself.
        var store = new JsonConfigStore(_directory);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        try
        {
            await store.SaveAsync(new AppSettings(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_cache_write_leaves_no_temp_file_and_never_throws()
    {
        var cache = new JsonSnapshotCache(_directory);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        UsageSnapshot snapshot = Fixture.Parse(Fixture.Normal);

        // Cancellation is the one failure the cache still propagates; everything
        // else is swallowed so a missing cache can never take the app down.
        try
        {
            await cache.SaveAsync(snapshot, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task The_cache_round_trips_through_the_generated_metadata()
    {
        // Pinned to the fixture's own time: the reading must not be discarded for
        // age. With the real clock this went red once the fixture date fell more
        // than the cache's two-day cap behind today.
        var cache = new JsonSnapshotCache(_directory, new FakeTimeProvider(Fixture.FixedNow));
        UsageSnapshot original = Fixture.Parse(Fixture.Normal);

        await cache.SaveAsync(original, Ct);
        UsageSnapshot? loaded = await cache.LoadAsync(Ct);

        loaded.Should().NotBeNull();
        loaded!.Session!.Percent.Should().Be(29d);
        loaded.WeekFable!.Percent.Should().Be(88d);
    }
}
