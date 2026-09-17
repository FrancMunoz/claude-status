using ClaudeStatus.Localization;
using ClaudeStatus.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// One Claude Code session, as a row on the card.
/// </summary>
/// <remarks>
/// The row is the folder, how long it has been going, and whether it is still
/// going. Not the session id: it is a UUID, it means nothing to anyone, and the
/// folder is what a person actually recognises their own work by.
/// </remarks>
public sealed partial class SessionRowViewModel : ObservableObject
{
    private readonly ILocalizer _l;

    public SessionRowViewModel(ILocalizer localizer, ClaudeSession session, DateTimeOffset now)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        ArgumentNullException.ThrowIfNull(session);

        Id = session.Id;
        IsRunning = session.IsRunning;
        CanFocus = session.IsRunning && session.Origin is not null;
        Name = session.Name.Length > 0 ? session.Name : _l["Sessions_Unnamed"];

        // Three states, not two. "Open" and "busy" are different things, and
        // calling both of them running contradicted the notification that had
        // just told the user this very session was waiting for them.
        StateText = _l[session.State switch
        {
            SessionState.Working => "Sessions_Working",
            SessionState.Waiting => "Sessions_Waiting",
            _ => "Sessions_Finished",
        }];
        DurationText = Describe(session.Duration(now));
    }

    /// <summary>Claude Code's session id. The identity, never shown.</summary>
    public string Id { get; }

    /// <summary>The folder's last segment.</summary>
    public string Name { get; }

    /// <summary>Running or finished, in the current language.</summary>
    public string StateText { get; }

    /// <summary>How long it has run, as <c>4m</c> or <c>1h 12m</c>.</summary>
    public string DurationText { get; }

    /// <summary>Whether it is still going, for the styling.</summary>
    public bool IsRunning { get; }

    /// <summary>Whether clicking the row can bring the session's terminal forward.</summary>
    /// <remarks>
    /// Only while the session runs and its terminal was recorded. A finished
    /// session's Claude Code has exited, and a session from before origins were
    /// recorded - or on a platform that records none - has nothing to focus.
    /// </remarks>
    public bool CanFocus { get; }

    /// <summary>Whether this session's notifications are silenced.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifies))]
    private bool _isMuted;

    /// <summary>
    /// The inverse of <see cref="IsMuted"/>, for a switch: on means "tell me".
    /// </summary>
    /// <remarks>
    /// A switch that is on to silence something reads backwards - every other
    /// switch in the OS turns a thing on. The checkbox in Config keeps "Silence".
    /// </remarks>
    public bool Notifies
    {
        get => !IsMuted;
        set => IsMuted = !value;
    }

    /// <summary>
    /// A duration at the resolution a person cares about.
    /// </summary>
    /// <remarks>
    /// Minutes below an hour, hours and minutes above it, and never seconds: a
    /// list that reticks every second draws the eye to the one thing on it that
    /// is not information.
    /// </remarks>
    private static string Describe(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        return duration < TimeSpan.FromHours(1)
            ? $"{(int)duration.TotalMinutes}m"
            : $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }
}
