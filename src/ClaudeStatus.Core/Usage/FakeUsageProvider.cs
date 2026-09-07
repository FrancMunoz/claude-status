namespace ClaudeStatus.Usage;

/// <summary>A scripted scenario for <see cref="FakeUsageProvider"/>.</summary>
public enum FakeUsageScenario
{
    /// <summary>Comfortable usage across the board.</summary>
    Healthy = 0,

    /// <summary>Session window above a default 80 % threshold, so the icon goes red.</summary>
    SessionNearLimit = 1,

    /// <summary>Fable window above threshold, the others calm.</summary>
    FableNearLimit = 2,

    /// <summary>A plan with no Fable-scoped window at all.</summary>
    NoFableWindow = 3,

    /// <summary>Every fetch fails with a network error.</summary>
    AlwaysFails = 4,

    /// <summary>Every fetch fails as rate limited, with a Retry-After.</summary>
    RateLimited = 5,

    /// <summary>Every fetch fails as unauthorized.</summary>
    Unauthorized = 6,
}

/// <summary>
/// A deterministic provider for development and UI work, selected with <c>--fake</c>.
/// </summary>
/// <remarks>
/// Deterministic on purpose: given the same clock reading it returns the same
/// snapshot, so screenshots and tests are reproducible. It never touches the
/// network and never reads a credential.
/// </remarks>
public sealed class FakeUsageProvider : IUsageProvider
{
    private readonly TimeProvider _clock;
    private int _fetchCount;

    public FakeUsageProvider(FakeUsageScenario scenario = FakeUsageScenario.Healthy, TimeProvider? clock = null)
    {
        Scenario = scenario;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Which scripted scenario this instance plays.</summary>
    public FakeUsageScenario Scenario { get; }

    /// <summary>How many times <see cref="FetchAsync"/> has been called. Handy in tests.</summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <inheritdoc />
    public string NameKey => "Provider_Fake";

    /// <inheritdoc />
    public object? NameArgument => Scenario;

    /// <inheritdoc />
    public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _fetchCount);

        DateTimeOffset now = _clock.GetUtcNow();

        return Scenario switch
        {
            FakeUsageScenario.AlwaysFails => Task.FromException<UsageSnapshot>(
                new UsageFetchException(UsageFetchFailure.Network, "Fake provider: simulated network failure.")),

            FakeUsageScenario.RateLimited => Task.FromException<UsageSnapshot>(
                new UsageFetchException(UsageFetchFailure.RateLimited, "Fake provider: simulated 429.")
                {
                    RetryAfter = TimeSpan.FromSeconds(120),
                }),

            FakeUsageScenario.Unauthorized => Task.FromException<UsageSnapshot>(
                new UsageFetchException(UsageFetchFailure.Unauthorized, "Fake provider: simulated 401.")),

            _ => Task.FromResult(Build(now)),
        };
    }

    private UsageSnapshot Build(DateTimeOffset now)
    {
        DateTimeOffset sessionReset = now.AddHours(2).AddMinutes(37);
        DateTimeOffset weekReset = now.AddDays(2).AddHours(6);

        (double session, double week, double? fable) = Scenario switch
        {
            FakeUsageScenario.SessionNearLimit => (91d, 58d, (double?)44d),
            FakeUsageScenario.FableNearLimit => (29d, 58d, (double?)88d),
            FakeUsageScenario.NoFableWindow => (29d, 58d, (double?)null),
            _ => (29d, 58d, (double?)44d),
        };

        return new UsageSnapshot(
            Session: UsageWindow.Create(session, sessionReset),
            Week: UsageWindow.Create(week, weekReset),
            WeekFable: fable is null ? null : UsageWindow.Create(fable.Value, weekReset),
            OtherWindows: new Dictionary<string, UsageWindow>(),
            FetchedAt: now,
            IsStale: false);
    }
}
