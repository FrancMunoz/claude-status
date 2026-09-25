using ClaudeStatus.Config;

namespace ClaudeStatus.Sessions;

/// <summary>
/// Which session changes are worth interrupting the user for.
/// </summary>
/// <remarks>
/// <para>
/// The settings half of that question, kept apart from the platform half. Whether
/// the session's own terminal is already in front can only be asked of the running
/// system; whether the user asked to hear about this kind of change at all is a
/// rule, and a rule with three ways to say no is worth being able to test.
/// </para>
/// <para>
/// The caller still owns the order: this is asked first, because it is free, and
/// the focus check - which walks processes - only for what survives it.
/// </para>
/// </remarks>
public static class SessionNotices
{
    /// <summary>Whether a change should be announced, going by the settings alone.</summary>
    /// <param name="change">What the registry made of the event.</param>
    /// <param name="sessionId">The session it happened to.</param>
    /// <param name="settings">The user's answers.</param>
    public static bool ShouldAnnounce(SessionChange change, string sessionId, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Appearing is not news - the user opened it - and None is the registry
        // saying nothing happened worth reacting to.
        if (change is not (SessionChange.Idle or SessionChange.Finished))
        {
            return false;
        }

        // Silenced by hand, from the popup or the config window. Whichever change
        // it is: a session someone is sitting in front of is not made interesting
        // by being closed.
        if (settings.MutedSessions.Contains(sessionId, StringComparer.Ordinal))
        {
            return false;
        }

        return change != SessionChange.Finished || settings.NotifyOnSessionEnd;
    }
}
