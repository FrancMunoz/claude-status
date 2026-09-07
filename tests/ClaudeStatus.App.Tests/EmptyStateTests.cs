using ClaudeStatus.App.ViewModels;
using ClaudeStatus.Localization;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// What the popup says when it has no numbers to show.
/// </summary>
/// <remarks>
/// This is the first thing a new user sees, and for a while it may be the only
/// thing: a fresh install with no Claude Code login never gets a reading at all
/// until somebody does something about it. Three empty bars and three dashes
/// left that person with no idea what.
/// </remarks>
public class EmptyStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Localizer L = TestLocalizer.English();

    private static DetailsViewModel Build(UsageFetchFailure? failure)
    {
        var model = new DetailsViewModel(
            new StubMonitor(failure), () => new AppSettings(), L, new FakeTimeProvider(Now));

        model.Apply(null);
        return model;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UsageFetchFailure.NoCredential)]
    [InlineData(UsageFetchFailure.Unauthorized)]
    [InlineData(UsageFetchFailure.RateLimited)]
    [InlineData(UsageFetchFailure.Network)]
    [InlineData(UsageFetchFailure.Unreadable)]
    [InlineData(UsageFetchFailure.ServerError)]
    public void Every_reason_for_having_no_reading_says_something(UsageFetchFailure? failure)
    {
        // Including the ones with no case of their own: a state that falls through
        // to the default must still explain itself, not go blank.
        using DetailsViewModel model = Build(failure);

        model.HasReading.Should().BeFalse();
        model.EmptyTitle.Should().NotBeNullOrWhiteSpace();
        model.EmptyBody.Should().NotBeNullOrWhiteSpace();
        model.EmptyBody.Should().NotBe(model.EmptyTitle, "the body must add something");
    }

    [Fact]
    public void The_first_poll_is_described_as_progress_rather_than_a_problem()
    {
        // Nothing has gone wrong here, and the popup opens in this state every
        // time the app starts. It must not look like a fault.
        using DetailsViewModel model = Build(failure: null);

        model.EmptyTitle.Should().Be("Reading your usage…");
        model.CanOpenConfig.Should().BeFalse("there is nothing to fix");
    }

    [Theory]
    [InlineData(UsageFetchFailure.NoCredential)]
    [InlineData(UsageFetchFailure.Unauthorized)]
    public void A_credential_problem_offers_the_window_that_fixes_it(UsageFetchFailure failure)
    {
        using DetailsViewModel model = Build(failure);

        model.CanOpenConfig.Should().BeTrue();
    }

    [Theory]
    [InlineData(UsageFetchFailure.RateLimited)]
    [InlineData(UsageFetchFailure.Network)]
    [InlineData(UsageFetchFailure.ServerError)]
    public void A_problem_that_resolves_itself_offers_no_button(UsageFetchFailure failure)
    {
        // Opening Config would show a perfectly good credential and no explanation
        // of why the numbers are missing, which is worse than offering nothing.
        using DetailsViewModel model = Build(failure);

        model.CanOpenConfig.Should().BeFalse();
    }

    [Fact]
    public void Each_reason_gets_its_own_wording()
    {
        // Otherwise the state machine exists but the user cannot see it.
        UsageFetchFailure?[] reasons =
        [
            null,
            UsageFetchFailure.NoCredential,
            UsageFetchFailure.Unauthorized,
            UsageFetchFailure.RateLimited,
            UsageFetchFailure.Network,
        ];

        var titles = new List<string>();
        foreach (UsageFetchFailure? reason in reasons)
        {
            using DetailsViewModel model = Build(reason);
            titles.Add(model.EmptyTitle);
        }

        titles.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_button_asks_the_controller_rather_than_opening_a_window_itself()
    {
        // The controller owns every window's lifetime, and it is the only thing
        // that can reuse an already-open Config rather than opening a second one.
        using DetailsViewModel model = Build(UsageFetchFailure.NoCredential);

        int raised = 0;
        model.ConfigRequested += (_, _) => raised++;
        model.OpenConfigCommand.Execute(null);

        raised.Should().Be(1);
    }

    [Fact]
    public void A_reading_arriving_clears_the_whole_empty_state()
    {
        using DetailsViewModel model = Build(UsageFetchFailure.NoCredential);

        model.Apply(new UsageSnapshot(
            Session: UsageWindow.Create(29d, Now.AddHours(1)),
            Week: UsageWindow.Create(58d, Now.AddDays(2)),
            WeekFable: null,
            OtherWindows: new Dictionary<string, UsageWindow>(),
            FetchedAt: Now,
            IsStale: false));

        model.HasReading.Should().BeTrue();
        model.EmptyTitle.Should().BeEmpty();
        model.EmptyBody.Should().BeEmpty();
        model.CanOpenConfig.Should().BeFalse();
    }

    /// <summary>A monitor that has never succeeded, stuck on one failure.</summary>
    private sealed class StubMonitor(UsageFetchFailure? failure) : IUsageMonitor
    {
        public UsageSnapshot? Latest => null;

        public UsageMonitorStatus Status { get; } = new(null, failure, TimeSpan.FromMinutes(1));

        public IObservable<UsageSnapshot> Snapshots { get; } = new Silent();

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public Task<bool> RefreshNowAsync(CancellationToken ct = default) => Task.FromResult(false);

        private sealed class Silent : IObservable<UsageSnapshot>
        {
            public IDisposable Subscribe(IObserver<UsageSnapshot> observer) => new Nothing();

            private sealed class Nothing : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }
}
