using ClaudeStatus.Platform;
using ClaudeStatus.Usage;

namespace ClaudeStatus.Export;

/// <summary>
/// Turns what the controller knows into a <see cref="UsageExport"/>.
/// </summary>
/// <remarks>
/// Pure, so the contract is testable without a window or a server: the same
/// inputs the indicators get in, the document a consumer sees out. The state
/// follows <see cref="IndicatorText.Absence"/> exactly, so a widget and the tray
/// icon never disagree about whether there is a reading.
/// </remarks>
public static class UsageExportMapper
{
    /// <summary>Builds the document for the current moment.</summary>
    /// <param name="snapshot">The latest reading, or null before the first.</param>
    /// <param name="alert">The indicator's verdict on the last fetch.</param>
    /// <param name="pace">The velocity tracker's current alert, if any.</param>
    /// <param name="sessions">Working and open session counts, or null while the watch is off.</param>
    /// <param name="thresholdPercent">The user's alert threshold.</param>
    /// <param name="now">The current time, from which the pace times are projected.</param>
    /// <param name="appVersion">The app's version string.</param>
    public static UsageExport Map(
        UsageSnapshot? snapshot,
        IndicatorAlert alert,
        VelocityAlert? pace,
        UsageExportSessions? sessions,
        double thresholdPercent,
        DateTimeOffset now,
        string appVersion)
    {
        ArgumentNullException.ThrowIfNull(appVersion);

        UsageExportState state = alert switch
        {
            IndicatorAlert.NeedsCredential => UsageExportState.NoCredential,
            IndicatorAlert.Unreachable => UsageExportState.Unreachable,
            _ when snapshot is null => UsageExportState.NoData,
            _ => UsageExportState.Ok,
        };

        List<UsageExportWindow> windows = [];
        if (snapshot is not null)
        {
            Add(windows, UsageExport.SessionWindowId, snapshot.Session, UsageWindowSpans.Session);
            Add(windows, UsageExport.WeekWindowId, snapshot.Week, UsageWindowSpans.Week);
            Add(windows, UsageExport.WeekFableWindowId, snapshot.WeekFable, UsageWindowSpans.Week);
        }

        return new UsageExport(
            UsageExport.CurrentSchema,
            state,
            appVersion,
            snapshot?.FetchedAt,
            snapshot?.IsStale ?? false,
            ThresholdEvaluator.Clamp(thresholdPercent),
            windows,
            pace is null ? null : MapPace(pace, now),
            sessions);
    }

    private static void Add(List<UsageExportWindow> windows, string id, UsageWindow? window, TimeSpan span)
    {
        if (window is null)
        {
            return;
        }

        windows.Add(new UsageExportWindow(
            id,
            window.Percent,
            window.ResetsAt,
            (int)span.TotalSeconds,
            IndicatorText.IsExhausted(window.Percent)));
    }

    private static UsageExportPace MapPace(VelocityAlert alert, DateTimeOffset now)
        => new(
            alert.Window == VelocityWindow.Session ? UsageExport.SessionWindowId : UsageExport.WeekWindowId,
            alert.PercentPerHour,
            now + alert.UntilExhausted,
            now + alert.UntilReset);
}
