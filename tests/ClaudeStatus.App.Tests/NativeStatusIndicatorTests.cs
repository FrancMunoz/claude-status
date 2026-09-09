using ClaudeStatus.App.Tray;
using ClaudeStatus.Platform;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// The macOS menu bar indicator, driven through a fake status item.
/// </summary>
/// <remarks>
/// The point of <see cref="INativeStatusItem"/> is that everything above it is
/// ordinary code: the text it writes, the menu it builds and the way a click is
/// routed can all be asserted without a menu bar to look at. Only the Objective-C
/// underneath needs a real Mac, and it is the thin part.
/// </remarks>
[Collection(HeadlessTests.Name)]
public class NativeStatusIndicatorTests(HeadlessAppFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    /// <summary>Records what the indicator asked the platform to show.</summary>
    private sealed class FakeStatusItem : INativeStatusItem
    {
        public string? Title { get; private set; }

        public StatusTint Tint { get; private set; }

        public IReadOnlyList<StatusMenuEntry> Menu { get; private set; } = [];

        public bool Visible { get; private set; }

        public bool Disposed { get; private set; }

        public bool IsAvailable => true;

        public event EventHandler? LeftClicked;

        public event EventHandler<long>? MenuItemClicked;

        /// <summary>The mark handed over, if any.</summary>
        public byte[] Icon { get; private set; } = [];

        public void SetTitle(string text, StatusTint tint) => (Title, Tint) = (text, tint);

        public void SetIcon(ReadOnlySpan<byte> png) => Icon = png.ToArray();

        public void SetMenu(IReadOnlyList<StatusMenuEntry> entries) => Menu = entries;

        public void SetVisible(bool visible) => Visible = visible;

        public void Dispose() => Disposed = true;

        public void RaiseLeftClick() => LeftClicked?.Invoke(this, EventArgs.Empty);

        public void RaiseMenuClick(long tag) => MenuItemClicked?.Invoke(this, tag);

        /// <summary>Finds an entry by label, at any depth.</summary>
        public StatusMenuEntry? Find(string title)
        {
            static StatusMenuEntry? Search(IReadOnlyList<StatusMenuEntry> entries, string title)
            {
                foreach (StatusMenuEntry entry in entries)
                {
                    if (entry.Title == title)
                    {
                        return entry;
                    }

                    if (entry.Submenu is { } children && Search(children, title) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }

            return Search(Menu, title);
        }
    }

    /// <summary>
    /// A reading whose windows reset at plausible times rather than at
    /// <see cref="Now"/>, so the session countdown in the title is a real one.
    /// </summary>
    private static UsageSnapshot Snapshot(
        double session = 42d, double week = 18d, double? fable = 3d, bool stale = false) => new(
            UsageWindow.Create(session, Now + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(11)),
            UsageWindow.Create(week, Now + TimeSpan.FromDays(2)),
            fable is null ? null : UsageWindow.Create(fable.Value, Now + TimeSpan.FromDays(2)),
            new Dictionary<string, UsageWindow>(),
            Now,
            stale);

    private static T OnUi<T>(Func<T> action) => HeadlessAppFixture.Invoke(action);

    private static (NativeStatusIndicator Indicator, FakeStatusItem Item) Build()
        => Build(out FakeTimeProvider _);

    private static (NativeStatusIndicator Indicator, FakeStatusItem Item) Build(
        out FakeTimeProvider clock)
    {
        var created = new FakeTimeProvider(Now);
        clock = created;

        return OnUi(() =>
        {
            var item = new FakeStatusItem();
            return (
                new NativeStatusIndicator(item, TestLocalizer.English(), log: null, clock: created),
                item);
        });
    }

    /// <summary>Renders a reading fetched <paramref name="age"/> ago.</summary>
    private static StatusTint TintAfter(TimeSpan age, bool stale)
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build(out FakeTimeProvider clock);
        using (indicator)
        {
            var snapshot = new UsageSnapshot(
                UsageWindow.Create(42d, Now),
                UsageWindow.Create(18d, Now),
                null,
                new Dictionary<string, UsageWindow>(),
                Now,
                stale);

            clock.SetUtcNow(Now + age);

            OnUi(() =>
            {
                indicator.Configure(new IndicatorOptions(
                    80d, ShowWeekFable: false, PollInterval: TimeSpan.FromMinutes(1)));
                indicator.Render(
                    snapshot, IndicatorMode.Row, ThresholdState.Normal, IndicatorAlert.None);
                return 0;
            });

            return item.Tint;
        }
    }

    [Fact]
    public void One_failed_poll_does_not_fade_a_reading_that_is_seconds_old()
    {
        // The endpoint 429s readily and the monitor marks a reading stale on the
        // first failure, so this used to fade the menu bar while showing a number
        // forty seconds old. Age is the question that was meant.
        TintAfter(TimeSpan.FromSeconds(40), stale: true).Should().Be(StatusTint.Normal);
    }

    [Fact]
    public void A_reading_nothing_has_refreshed_for_minutes_does_fade()
    {
        TintAfter(TimeSpan.FromMinutes(10), stale: true).Should().Be(StatusTint.Stale);
    }

    [Fact]
    public void An_old_reading_the_monitor_is_happy_with_is_not_faded()
    {
        // Both conditions are required. Age alone would fade a perfectly good
        // reading in the gap between a slow poll and its reply.
        TintAfter(TimeSpan.FromMinutes(10), stale: false).Should().Be(StatusTint.Normal);
    }

    [Fact]
    public void The_fade_threshold_follows_the_configured_poll_interval()
    {
        // A long interval means a reading is expected to be old, and fading one
        // that arrived exactly on schedule would mark every reading doubtful.
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build(out FakeTimeProvider clock);
        using (indicator)
        {
            var snapshot = new UsageSnapshot(
                UsageWindow.Create(42d, Now),
                UsageWindow.Create(18d, Now),
                null,
                new Dictionary<string, UsageWindow>(),
                Now,
                IsStale: true);

            clock.SetUtcNow(Now + TimeSpan.FromMinutes(20));

            OnUi(() =>
            {
                indicator.Configure(new IndicatorOptions(
                    80d, ShowWeekFable: false, PollInterval: TimeSpan.FromMinutes(15)));
                indicator.Render(
                    snapshot, IndicatorMode.Row, ThresholdState.Normal, IndicatorAlert.None);
                return 0;
            });

            item.Tint.Should().Be(
                StatusTint.Normal, "20 minutes is well inside three 15-minute intervals");
        }
    }

    [Fact]
    public void The_row_writes_both_limits_with_their_labels_and_a_percent_sign()
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            OnUi(() =>
            {
                indicator.Render(
                    Snapshot(), IndicatorMode.Row, ThresholdState.Normal, IndicatorAlert.None);
                return 0;
            });
        }

        item.Title.Should().Be("5h (2:11) 42% · 7d 18%");
    }

    [Fact]
    public void The_Fable_pair_appears_only_when_it_was_asked_for()
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            OnUi(() =>
            {
                indicator.Configure(new IndicatorOptions(80d, ShowWeekFable: true));
                indicator.Render(
                    Snapshot(), IndicatorMode.Row, ThresholdState.Normal, IndicatorAlert.None);
                return 0;
            });
        }

        item.Title.Should().Be("5h (2:11) 42% · 7d 18% · F 3%");
    }

    [Fact]
    public void A_left_click_asks_for_the_details_rather_than_the_menu()
    {
        // The whole reason this indicator exists. Avalonia's status item gives its
        // menu every click, so the popup could only ever be reached through it.
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            int clicks = 0;
            ContextAction? action = null;
            indicator.LeftClicked += (_, _) => clicks++;
            indicator.MenuAction += (_, e) => action = e.Action;

            item.RaiseLeftClick();

            clicks.Should().Be(1);
            action.Should().BeNull("a left click is not a menu action");
        }
    }

    [Theory]
    [InlineData(ContextAction.ShowDetails)]
    [InlineData(ContextAction.ShowReport)]
    [InlineData(ContextAction.Refresh)]
    [InlineData(ContextAction.OpenConfig)]
    [InlineData(ContextAction.ShowInfo)]
    [InlineData(ContextAction.Quit)]
    public void Every_menu_command_survives_the_trip_through_a_native_tag(ContextAction expected)
    {
        // A native menu item carries one integer back and nothing else, so the tag
        // encoding is the only thing standing between "Quit" and "Refresh".
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            ContextAction? received = null;
            indicator.MenuAction += (_, e) => received = e.Action;

            StatusMenuEntry entry = item.Menu
                .First(e => !e.IsSeparator && e.Tag == 100 + (long)expected);
            item.RaiseMenuClick(entry.Tag);

            received.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData(IndicatorMode.Row)]
    [InlineData(IndicatorMode.SessionPercent)]
    [InlineData(IndicatorMode.WeekPercent)]
    [InlineData(IndicatorMode.WeekFablePercent)]
    public void Every_mode_survives_the_trip_too_and_stays_clear_of_the_commands(IndicatorMode mode)
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            IndicatorMode? received = null;
            ContextAction? action = null;
            indicator.MenuAction += (_, e) => (action, received) = (e.Action, e.Mode);

            item.RaiseMenuClick(200 + (long)mode);

            action.Should().Be(ContextAction.ChangeMode);
            received.Should().Be(mode);
        }
    }

    [Fact]
    public void An_unrecognised_tag_is_ignored_rather_than_cast_into_a_command()
    {
        // Tags come back from AppKit as plain integers. A stale menu left over from
        // a previous build must not turn into whichever action happens to sit at
        // that number.
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            var raised = false;
            indicator.MenuAction += (_, _) => raised = true;

            item.RaiseMenuClick(100 + 999);
            item.RaiseMenuClick(200 + 999);

            raised.Should().BeFalse();
        }
    }

    [Fact]
    public void The_status_button_own_tag_is_not_mistaken_for_a_menu_command()
    {
        // -1 is what an NSStatusBarButton actually reports for its tag, and routing
        // clicks by "tag == 0 means the button" sent every click here instead, where
        // it decoded to no command and vanished. The platform layer now identifies
        // the button by identity; this pins the value that broke it.
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            var raised = false;
            indicator.MenuAction += (_, _) => raised = true;

            item.RaiseMenuClick(-1);

            raised.Should().BeFalse();
        }
    }

    [Fact]
    public void The_menu_keeps_the_tick_on_the_mode_actually_showing()
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            OnUi(() =>
            {
                indicator.Render(
                    Snapshot(), IndicatorMode.WeekPercent, ThresholdState.Normal, IndicatorAlert.None);
                return 0;
            });

            item.Find("Week %")!.IsChecked.Should().BeTrue();
            item.Find("All in a row")!.IsChecked.Should().BeFalse();
        }
    }

    [Fact]
    public void Details_is_still_the_first_entry_for_anyone_not_using_a_mouse()
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            item.Menu[0].Title.Should().Be("Details");
        }
    }

    [Fact]
    public void Past_the_threshold_the_row_is_tinted_rather_than_left_to_the_menu_bar()
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            OnUi(() =>
            {
                indicator.Render(
                    Snapshot(session: 95d), IndicatorMode.Row,
                    ThresholdState.Exceeded, IndicatorAlert.None);
                return 0;
            });
        }

        item.Tint.Should().Be(StatusTint.Alert);
    }

    [Fact]
    public void A_stale_reading_that_is_over_the_threshold_stays_red_rather_than_dimming()
    {
        // Dimming would soften the warning in the one case it matters most: the
        // reading is old, but it is still the best evidence the user is near their
        // limit.
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            OnUi(() =>
            {
                indicator.Render(
                    Snapshot(session: 95d, stale: true), IndicatorMode.Row,
                    ThresholdState.Exceeded, IndicatorAlert.None);
                return 0;
            });
        }

        item.Tint.Should().Be(StatusTint.Alert);
    }

    [Fact]
    public void A_single_metric_mode_writes_one_labelled_reading()
    {
        // The row is the default, not the only choice: someone who wants one
        // number in their menu bar keeps that option.
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            OnUi(() =>
            {
                indicator.Render(
                    Snapshot(), IndicatorMode.WeekPercent,
                    ThresholdState.Normal, IndicatorAlert.None);
                return 0;
            });
        }

        item.Title.Should().Be("7d 18%");
    }

    [Fact]
    public void The_ring_has_no_text_form_and_falls_back_to_the_session_reading()
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            OnUi(() =>
            {
                indicator.Render(
                    Snapshot(), IndicatorMode.Ring, ThresholdState.Normal, IndicatorAlert.None);
                return 0;
            });
        }

        item.Title.Should().Be("5h (2:11) 42%");
    }

    [Fact]
    public void The_mark_is_handed_to_the_platform_before_any_reading_arrives()
    {
        // It is branding, not state: it must be there from the first frame, not
        // appear once the first fetch lands.
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        using (indicator)
        {
            item.Icon.Should().NotBeEmpty();

            // PNG magic. Cheap, and it catches the encoder silently changing format
            // under a platform that will only accept image bytes it recognises.
            item.Icon.Take(4).Should().Equal(0x89, (byte)'P', (byte)'N', (byte)'G');
        }
    }

    [Fact]
    public void Disposing_the_indicator_takes_the_item_out_of_the_menu_bar()
    {
        (NativeStatusIndicator indicator, FakeStatusItem item) = Build();
        indicator.Dispose();

        item.Disposed.Should().BeTrue();
    }

    [Fact]
    public void The_fixture_is_what_boots_Avalonia_for_this_collection()
        => fixture.Should().NotBeNull();
}
