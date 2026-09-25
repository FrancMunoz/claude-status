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

    /// <summary>
    /// The scripted day both platforms pose for, so their screenshots can be put
    /// side by side.
    /// </summary>
    /// <remarks>
    /// The same comfortable readings as <see cref="Healthy"/> - the numbers the
    /// README's images have always carried - and one difference that only matters
    /// with a camera pointed at it: the session window's reset carries most of a
    /// minute of slack, so the countdown does not tick over between the shot taken
    /// on the Mac and the one taken on the PC. See
    /// <c>FakeUsageProvider.ScreenshotSlack</c>.
    /// </remarks>
    Screenshot = 7,
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

    /// <summary>
    /// What <see cref="FakeUsageScenario.Screenshot"/> adds to the session reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The countdown is written by truncation - <c>2h37m50s</c> reads
    /// <c>(2:37)</c> - and it is recomputed from the stored reset at every render,
    /// so between one poll and the next it slides towards the minute below. On the
    /// whole minute that happens ten seconds in; with fifty seconds of slack it
    /// happens fifty seconds in, by which time the default one-minute poll has
    /// already put it back. The countdown therefore reads the same for all but a
    /// few seconds of each cycle, which is what lets two machines photographed
    /// minutes apart agree.
    /// </para>
    /// <para>
    /// It cannot be made exact. A minute-resolution countdown and a one-minute
    /// poll are the same width, so there is no offset that never crosses a
    /// boundary; this narrows the window where it disagrees from ten seconds in
    /// sixty to a few. <c>docs/screenshots.md</c> says to refresh and then shoot,
    /// which closes it.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ScreenshotSlack = TimeSpan.FromSeconds(50);

    private UsageSnapshot Build(DateTimeOffset now)
    {
        DateTimeOffset sessionReset = now.AddHours(2).AddMinutes(37)
            + (Scenario == FakeUsageScenario.Screenshot ? ScreenshotSlack : TimeSpan.Zero);
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
