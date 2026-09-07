using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// The full report window: everything the endpoint returned, not just the three
/// numbers the popup shows.
/// </summary>
public class ReportViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static UsageSnapshot Snapshot(
        SpendInfo? spend = null, ExtraUsageInfo? credits = null, bool stale = false) => new(
        Session: UsageWindow.Create(29d, Now.AddHours(2)) with
        {
            Severity = "normal",
            Kind = "session",
            IsActive = false,
        },
        Week: UsageWindow.Create(58d, Now.AddDays(2)) with
        {
            Severity = "normal",
            Kind = "weekly_all",
            IsActive = false,
        },
        WeekFable: UsageWindow.Create(88d, Now.AddDays(2)) with
        {
            Severity = "warning",
            Kind = "weekly_scoped",
            IsActive = true,
            ScopeModel = "Fable",
        },
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: Now,
        IsStale: stale)
        {
            Spend = spend,
            ExtraUsage = credits,
        };

    private static (ReportViewModel Model, UsageMonitor Monitor) Build(
        UsageSnapshot? seed = null, Localizer? localizer = null)
    {
        var clock = new FakeTimeProvider(Now.AddMinutes(5));
        var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        var model = new ReportViewModel(
            monitor,
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock),
            new ReportPlatformInfo(),
            localizer ?? TestLocalizer.English(),
            clock);

        model.Apply(seed);
        return (model, monitor);
    }

    [Fact]
    public void Every_window_becomes_a_row()
    {
        (ReportViewModel model, UsageMonitor monitor) = Build(Snapshot());
        using (monitor)
        {
            model.Rows.Should().HaveCount(3);
            model.Rows[0].Label.Should().Be("Session");
            model.Rows[2].Label.Should().Be("Week (Fable)");
        }
    }

    [Fact]
    public void A_row_shows_the_sources_own_severity_and_kind_verbatim()
    {
        // Not translated and not mapped to an enum: it is an open set from an
        // undocumented endpoint, and showing what arrived is the honest answer.
        (ReportViewModel model, UsageMonitor monitor) = Build(Snapshot());
        using (monitor)
        {
            model.Rows[2].Severity.Should().Be("warning");
            model.Rows[2].Kind.Should().Be("weekly_scoped");
            model.Rows[2].IsWarning.Should().BeTrue();
            model.Rows[0].IsWarning.Should().BeFalse();
        }
    }

    [Fact]
    public void A_window_with_no_severity_reads_as_unknown_rather_than_normal()
    {
        UsageSnapshot bare = new(
            Session: UsageWindow.Create(10d, null),
            Week: null, WeekFable: null,
            OtherWindows: new Dictionary<string, UsageWindow>(),
            FetchedAt: Now, IsStale: false);

        (ReportViewModel model, UsageMonitor monitor) = Build(bare);
        using (monitor)
        {
            model.Rows[0].Severity.Should().Be("—");
            model.Rows[0].Kind.Should().Be("—");
        }
    }

    [Fact]
    public void The_active_window_is_flagged()
    {
        (ReportViewModel model, UsageMonitor monitor) = Build(Snapshot());
        using (monitor)
        {
            model.Rows[2].IsActive.Should().BeTrue();
            model.Rows[2].ActiveText.Should().Be("Yes");
            model.Rows[0].ActiveText.Should().Be("No");
        }
    }

    [Fact]
    public void A_row_with_no_dollar_figures_hides_that_line_instead_of_showing_dashes()
    {
        (ReportViewModel model, UsageMonitor monitor) = Build(Snapshot());
        using (monitor)
        {
            model.Rows[0].HasDollars.Should().BeFalse();
            model.Rows[0].DollarsText.Should().BeEmpty();
        }
    }

    [Fact]
    public void Dollar_figures_are_shown_when_the_plan_reports_them()
    {
        UsageSnapshot withMoney = Snapshot() with
        {
            Session = UsageWindow.Create(29d, Now) with
            {
                UsedDollars = 12.5d,
                LimitDollars = 50d,
                RemainingDollars = 37.5d,
            },
        };

        (ReportViewModel model, UsageMonitor monitor) = Build(withMoney);
        using (monitor)
        {
            model.Rows[0].HasDollars.Should().BeTrue();
            model.Rows[0].DollarsText.Should().Contain("12.50").And.Contain("50.00");
        }
    }

    [Fact]
    public void Spending_that_is_off_says_so_rather_than_leaving_the_section_blank()
    {
        // A user wondering whether they are being charged wants that answered.
        (ReportViewModel model, UsageMonitor monitor) = Build(
            Snapshot(spend: new SpendInfo(false, null, null, 0d, "normal", null)));

        using (monitor)
        {
            model.SpendEnabled.Should().BeFalse();
            model.SpendText.Should().Contain("not being charged");
        }
    }

    [Fact]
    public void Spending_that_is_on_reports_the_amount_and_the_cap()
    {
        (ReportViewModel model, UsageMonitor monitor) = Build(
            Snapshot(spend: new SpendInfo(
                true,
                new MoneyAmount(1234, "USD", 2),
                new MoneyAmount(5000, "USD", 2),
                24.68d,
                "normal",
                null)));

        using (monitor)
        {
            model.SpendEnabled.Should().BeTrue();
            model.SpendText.Should().Contain("12.34").And.Contain("50.00").And.Contain("USD");
        }
    }

    [Fact]
    public void A_missing_spend_block_is_worded_differently_from_a_disabled_one()
    {
        (ReportViewModel absent, UsageMonitor first) = Build(Snapshot());
        (ReportViewModel off, UsageMonitor second) = Build(
            Snapshot(spend: new SpendInfo(false, null, null, null, null, null)));

        using (first)
        using (second)
        {
            absent.SpendText.Should().NotBe(off.SpendText);
        }
    }

    [Fact]
    public void Credits_distinguish_never_used_from_turned_off()
    {
        // They lead to different next actions, so they get different sentences.
        (ReportViewModel never, UsageMonitor first) = Build(
            Snapshot(credits: new ExtraUsageInfo(false, false, false, false, null, null, null, null, null)));
        (ReportViewModel off, UsageMonitor second) = Build(
            Snapshot(credits: new ExtraUsageInfo(false, true, false, true, null, null, null, null, null)));

        using (first)
        using (second)
        {
            never.CreditsText.Should().NotBe(off.CreditsText);
            never.CreditsText.Should().Contain("Not set up");
            off.CreditsText.Should().Contain("switched off");
        }
    }

    [Fact]
    public void A_reached_credit_limit_raises_a_warning()
    {
        (ReportViewModel model, UsageMonitor monitor) = Build(
            Snapshot(credits: new ExtraUsageInfo(true, false, true, true, 100d, 100d, 100d, "USD", null)));

        using (monitor)
        {
            model.CreditsLimitReached.Should().BeTrue();
        }
    }

    [Fact]
    public void With_no_reading_the_window_says_so_instead_of_showing_empty_rows()
    {
        (ReportViewModel model, UsageMonitor monitor) = Build(seed: null);
        using (monitor)
        {
            model.HasReading.Should().BeFalse();
            model.Rows.Should().BeEmpty();
            model.FreshnessText.Should().Contain("Waiting");
        }
    }

    [Fact]
    public void A_stale_reading_is_flagged()
    {
        (ReportViewModel model, UsageMonitor monitor) = Build(Snapshot(stale: true));
        using (monitor)
        {
            model.HasStaleReading.Should().BeTrue();
        }
    }

    [Fact]
    public void No_reading_is_not_a_stale_one()
    {
        // The banner this drives says "these are the last known values". Before
        // the first poll returns there are no values at all, and claiming there
        // are is worse than saying nothing - the snapshot's own IsStale is true
        // in that state, which is why this cannot just be passed through.
        (ReportViewModel model, UsageMonitor monitor) = Build(seed: null);
        using (monitor)
        {
            model.HasStaleReading.Should().BeFalse();
        }
    }

    [Fact]
    public void Changing_language_rebuilds_every_row_and_section()
    {
        var localizer = TestLocalizer.English();
        (ReportViewModel model, UsageMonitor monitor) = Build(Snapshot(), localizer);

        using (monitor)
        {
            model.Rows[0].Label.Should().Be("Session");
            model.Rows[0].ActiveText.Should().Be("No");

            localizer.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("es"));

            model.Rows[0].Label.Should().Be("Sesión");
            model.Rows[0].ActiveText.Should().Be("No", "Spanish for 'no' is also 'No'");
            model.Rows[2].ActiveText.Should().Be("Sí");
        }
    }

    [Fact]
    public void The_report_never_fetches_on_its_own()
    {
        // Opening a window must not add load to a rate-limited endpoint. The only
        // request it can cause is the explicit Refresh button.
        var clock = new FakeTimeProvider(Now);
        var provider = new FakeUsageProvider(FakeUsageScenario.Healthy, clock);
        using var monitor = new UsageMonitor(provider, new PollingOptions(), clock);

        using var model = new ReportViewModel(
            monitor, provider, new ReportPlatformInfo(), TestLocalizer.English(), clock);

        provider.FetchCount.Should().Be(0);
    }

    private sealed class ReportPlatformInfo : IPlatformInfo
    {
        public PlatformKind Kind => PlatformKind.Windows;

        public string OperatingSystemName => "Test OS";

        public string ConfigDirectory => Path.Combine(Path.GetTempPath(), "claudestatus-uitests");

        public TraySupport TraySupport => TraySupport.Available;

        public string? ExecutablePath => Environment.ProcessPath;

        public bool SupportsInlineTrayText => false;

        public bool TrayIsAtTop => false;
    }
}

/// <summary>
/// The Fable bar when its reset is really the weekly one.
/// </summary>
/// <remarks>
/// The live account that prompted this has 0 % Fable usage and gets
/// <c>resets_at: null</c> for that window, so the bar read "reset time unknown" -
/// which sounds like a fault rather than the endpoint declining to repeat itself.
/// </remarks>
public class SharedWeeklyResetDisplayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Localizer L = TestLocalizer.English();

    private static UsageSnapshot Snapshot(DateTimeOffset? fableReset) => new(
        Session: UsageWindow.Create(94d, Now.AddMinutes(55)),
        Week: UsageWindow.Create(17d, Now.AddDays(1).AddHours(11)),
        WeekFable: UsageWindow.Create(0d, fableReset),
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: Now,
        IsStale: false);

    [Fact]
    public void The_popup_says_it_resets_with_the_weekly_limit_instead_of_unknown()
    {
        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new DetailsViewModel(monitor, () => new AppSettings(), L, clock);
        viewModel.Apply(Snapshot(fableReset: null));

        viewModel.WeekFable.ResetText.Should().Be("resets with the weekly limit");
        viewModel.WeekFable.ResetText.Should().NotContain("unknown");
    }

    [Fact]
    public void The_weekly_bar_itself_still_shows_a_real_countdown()
    {
        // The message only replaces the Fable line. The user still has to be able
        // to see when the week actually rolls over.
        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new DetailsViewModel(monitor, () => new AppSettings(), L, clock);
        viewModel.Apply(Snapshot(fableReset: null));

        viewModel.Week.ResetText.Should().StartWith("resets in");
    }

    [Fact]
    public void A_Fable_window_with_its_own_schedule_keeps_its_countdown()
    {
        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new DetailsViewModel(monitor, () => new AppSettings(), L, clock);
        viewModel.Apply(Snapshot(Now.AddHours(3)));

        viewModel.WeekFable.ResetText.Should().Be("resets in 3h 0m");
    }

    [Fact]
    public void The_report_row_says_the_same_thing_but_keeps_the_raw_timestamp_honest()
    {
        // The report exists to show what the endpoint said. The countdown becomes
        // useful; the exact-time line still reports that nothing was given.
        var row = new ReportRowViewModel(
            L, "Week (Fable)", UsageWindow.Create(0d, null), Now, sharesWeeklyReset: true);

        row.ResetText.Should().Be("resets with the weekly limit");
        row.ResetsAtText.Should().Be("No reset time reported");
    }
}
