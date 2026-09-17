using System.Text.Json.Nodes;

namespace ClaudeStatus.Sessions;

/// <summary>
/// The hook entries ClaudeStatus keeps in Claude Code's settings file.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that knows Claude Code's hook schema. It is another
/// application's configuration file, written by us on the user's behalf, so two
/// rules govern everything here:
/// </para>
/// <list type="number">
/// <item>
/// <b>Only our own entries are ever touched.</b> They are found by the marker
/// argument <see cref="Marker"/>, never by the executable path - the path moves
/// on every update, and matching on it would orphan the entries it was meant to
/// find and then add a second copy beside them.
/// </item>
/// <item>
/// <b>Everything else survives untouched</b>, including keys and events this
/// version has never heard of. The document is edited as a DOM rather than
/// deserialized into a model of our own, because a model can only preserve what
/// it knows about.
/// </item>
/// </list>
/// <para>
/// The exec form (<c>command</c> plus <c>args</c>) is not decoration. Claude Code
/// spawns that form directly rather than through a shell, which is what stops a
/// console window flashing at the end of every turn - the thing that made the
/// hand-written PowerShell version of this unbearable to use.
/// </para>
/// </remarks>
public static class ClaudeCodeHooks
{
    /// <summary>
    /// The argument that marks a hook as ours.
    /// </summary>
    /// <remarks>
    /// Inert - the app only needs to know which event fired - and that is the
    /// point: it is an identity, readable by a human looking at their own
    /// settings file, and stable across updates, reinstalls and renames.
    /// </remarks>
    public const string Marker = "--claude-status-hook";

    /// <summary>Hook events we install, and the event name we pass back to ourselves.</summary>
    /// <remarks>
    /// <c>SessionStart</c> and <c>SessionEnd</c> bracket a session;
    /// <c>Stop</c> is both the heartbeat that keeps it on the list and the moment
    /// worth telling the user about. <c>Notification</c> is there for its
    /// <c>idle_prompt</c> only - see <see cref="SessionEventKind.Idled"/>.
    /// </remarks>
    public static readonly IReadOnlyList<string> Events =
        ["SessionStart", "UserPromptSubmit", "Stop", "Notification", "SessionEnd"];

    /// <summary>The <c>notification_type</c> that means a session is waiting at its prompt.</summary>
    public const string IdlePromptNotification = "idle_prompt";

    /// <summary>Maps a Claude Code event name to what it means to us.</summary>
    public static SessionEventKind? KindFor(string? hookEvent) => hookEvent switch
    {
        "SessionStart" => SessionEventKind.Started,
        "UserPromptSubmit" => SessionEventKind.Submitted,
        "Stop" => SessionEventKind.Progressed,
        "Notification" => SessionEventKind.Idled,
        "SessionEnd" => SessionEventKind.Ended,
        _ => null,
    };

    /// <summary>
    /// What one hook invocation means, given its payload's <c>notification_type</c>.
    /// </summary>
    /// <remarks>
    /// <c>Notification</c> covers several things, and most of them do not mean the
    /// turn is over: a permission prompt is Claude waiting in the middle of one, and
    /// calling that idle would take the badge down while the work carries on after
    /// the user approves. Only <see cref="IdlePromptNotification"/> is taken.
    /// </remarks>
    public static SessionEventKind? KindFor(string? hookEvent, string? notificationType)
        => KindFor(hookEvent) is SessionEventKind.Idled && notificationType != IdlePromptNotification
            ? null
            : KindFor(hookEvent);

    /// <summary>
    /// Whether this event's hook may be fired and forgotten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything per-turn is asynchronous: it writes one small file, nothing
    /// reads its output, and the end of somebody's turn must not wait on a
    /// notification.
    /// </para>
    /// <para>
    /// <b><c>SessionEnd</c> is the exception, and it has to be.</b> It fires while
    /// Claude Code is shutting down, and an asynchronous hook spawned moments
    /// before its parent exits does not survive to write anything - measured on
    /// this machine, every real session reported its turns and not one ever
    /// reported ending, so sessions sat on "running" until retention forgot them.
    /// Synchronous costs one process start, once, at exit.
    /// </para>
    /// </remarks>
    private static bool IsAsync(string hookEvent) => hookEvent != "SessionEnd";

    /// <summary>Builds the hook group for one event.</summary>
    private static JsonObject Group(string hookEvent, string executablePath) => new()
    {
        ["hooks"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "command",
                ["command"] = executablePath,
                ["args"] = new JsonArray(Marker, hookEvent),

                // Short: it writes one small file and exits. A hook that hangs
                // would hang the end of somebody's turn.
                ["timeout"] = 10,
                ["async"] = IsAsync(hookEvent),
            }),
    };

    /// <summary>Whether this hook entry is one of ours.</summary>
    private static bool IsOurs(JsonNode? entry)
    {
        if (entry is not JsonObject group || group["hooks"] is not JsonArray hooks)
        {
            return false;
        }

        foreach (JsonNode? hook in hooks)
        {
            if (hook is JsonObject obj
                && obj["args"] is JsonArray args
                && args.Any(a => a?.GetValue<string>() == Marker))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites <paramref name="settings"/> so that it holds exactly our hooks,
    /// or none of them.
    /// </summary>
    /// <param name="settings">
    /// The parsed settings document, edited in place. A null or non-object node
    /// is replaced with a fresh object.
    /// </param>
    /// <param name="enabled">Whether our hooks should be present at all.</param>
    /// <param name="executablePath">What the hooks should invoke.</param>
    /// <returns>True when the document changed.</returns>
    /// <remarks>
    /// Removal comes first and unconditionally, so a repointed executable, a
    /// renamed event or a hook left behind by an older version is cleaned up
    /// rather than duplicated. Empty containers are removed as well: an empty
    /// <c>hooks</c> object left in a settings file is litter we put there.
    /// </remarks>
    public static bool Reconcile(JsonObject settings, bool enabled, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        string before = settings.ToJsonString();

        if (settings["hooks"] is not JsonObject hooks)
        {
            if (!enabled)
            {
                // Nothing of ours can be in a file with no hooks at all, and
                // adding an empty object to say so would be litter.
                return false;
            }

            hooks = [];
            settings["hooks"] = hooks;
        }

        // Every event, not just the ones we install now: an event we used to use
        // and no longer do still has to be cleaned up.
        foreach (string hookEvent in hooks.Select(pair => pair.Key).ToArray())
        {
            if (hooks[hookEvent] is not JsonArray groups)
            {
                continue;
            }

            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (IsOurs(groups[i]))
                {
                    groups.RemoveAt(i);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(hookEvent);
            }
        }

        if (enabled)
        {
            foreach (string hookEvent in Events)
            {
                if (hooks[hookEvent] is not JsonArray groups)
                {
                    groups = [];
                    hooks[hookEvent] = groups;
                }

                groups.Add((JsonNode)Group(hookEvent, executablePath));
            }
        }
        else if (hooks.Count == 0)
        {
            settings.Remove("hooks");
        }

        return settings.ToJsonString() != before;
    }

    /// <summary>Whether the document already holds a hook of ours.</summary>
    public static bool IsInstalled(JsonObject? settings)
    {
        if (settings?["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        return hooks.Any(pair => pair.Value is JsonArray groups && groups.Any(IsOurs));
    }
}
