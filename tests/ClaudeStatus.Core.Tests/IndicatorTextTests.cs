using ClaudeStatus.Platform;
using ClaudeStatus.Usage;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The indicator's shared vocabulary: the tokens the widget and the macOS menu
/// bar both write, and which must therefore mean the same thing on both.
/// </summary>
public class IndicatorTextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(0, 0, "(0:00)")]
    [InlineData(0, 5, "(0:05)")]
    [InlineData(2, 11, "(2:11)")]
    [InlineData(23, 59, "(23:59)")]
    public void A_countdown_is_bracketed_hours_and_padded_minutes(int hours, int minutes, string expected)
        => IndicatorText.FormatCountdown(new TimeSpan(hours, minutes, 0)).Should().Be(expected);

    [Fact]
    public void Nothing_a_day_or_further_out_gets_a_clock()
    {
        IndicatorText.FormatCountdown(TimeSpan.FromDays(1)).Should().BeEmpty("(24:00) is not a time");
        IndicatorText.FormatCountdown(TimeSpan.FromDays(6)).Should().BeEmpty();
        IndicatorText.FormatCountdown(null).Should().BeEmpty("the source gave no reset time");
    }

    [Fact]
    public void A_reset_time_already_passed_reads_as_none_left_not_as_a_negative()
    {
        IndicatorText.FormatCountdown(TimeSpan.FromMinutes(-3)).Should().Be("(0:00)");
    }

    [Fact]
    public void The_row_carries_the_session_countdown_only_when_it_is_given_the_time()
    {
        var snapshot = new UsageSnapshot(
            UsageWindow.Create(56, Now + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(11)),
            UsageWindow.Create(18, Now + TimeSpan.FromDays(2)),
            null,
            new Dictionary<string, UsageWindow>(),
            Now,
            false);

        IndicatorText.ComposeRow(snapshot, IndicatorAlert.None, ("5h", "7d", "F"), false)
            .Should().Be("5h 56% · 7d 18%", "no clock was passed, so the row is as it always was");

        IndicatorText.ComposeRow(snapshot, IndicatorAlert.None, ("5h", "7d", "F"), false, now: Now)
            .Should().Be("5h (2:11) 56% · 7d 18%", "and the weekly window is too long for one");
    }

    [Fact]
    public void A_symbol_never_gets_a_countdown_beside_it()
    {
        // "5h (2:11) !" would dress the absence of a reading up as one.
        var snapshot = new UsageSnapshot(
            UsageWindow.Create(56, Now + TimeSpan.FromHours(2)),
            UsageWindow.Create(18, Now + TimeSpan.FromDays(2)),
            null,
            new Dictionary<string, UsageWindow>(),
            Now,
            false);

        IndicatorText
            .ComposeRow(snapshot, IndicatorAlert.NeedsCredential, ("5h", "7d", "F"), false, now: Now)
            .Should().Be("5h ! · 7d !");
    }
}
