using System.Text.Json;

namespace ClaudeStatus.Sessions;

/// <summary>
/// Remembers the session list across restarts.
/// </summary>
/// <remarks>
/// <para>
/// Without this the registry lives only in memory, and the list is emptied by
/// every restart, every update and every crash. That is wrong for a feature
/// whose entire question is "what is running": the sessions did not stop when
/// ClaudeStatus did, and a list that has forgotten them is not empty because
/// nothing is running - it is empty because we were not watching. Worse, nothing
/// would come back until each session's *next* turn, so a session left working
/// on something long would be invisible for exactly as long as it was busy.
/// </para>
/// <para>
/// Small, disposable state: a failed read or write loses the list, and losing
/// the list is what happened before this existed. So nothing here throws.
/// </para>
/// </remarks>
public sealed class SessionStore
{
    /// <summary>The file, under the app's own config directory.</summary>
    public const string FileName = "sessions.json";

    private readonly string _path;

    /// <param name="configDirectory">The app's own config directory.</param>
    public SessionStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _path = Path.Combine(configDirectory, FileName);
    }

    /// <summary>The file being written, for diagnostics.</summary>
    public string FilePath => _path;

    /// <summary>Reads what was remembered. Empty when there is nothing, or nothing readable.</summary>
    public IReadOnlyList<ClaudeSession> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            StoredSession[]? stored =
                JsonSerializer.Deserialize(File.ReadAllText(_path), SessionsJsonContext.Default.StoredSessionArray);

            if (stored is null)
            {
                return [];
            }

            return
            [
                .. stored
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                    .Select(s => new ClaudeSession(
                        s.Id!, s.Folder ?? string.Empty, s.StartedAt, s.LastSeenAt, s.EndedAt, Origin: s.Origin?.ToOrigin())),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes the list, replacing whatever was there.</summary>
    public void Save(IReadOnlyList<ClaudeSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");

            StoredSession[] stored =
            [
                .. sessions.Select(s => new StoredSession
                {
                    Id = s.Id,
                    Folder = s.Folder,
                    StartedAt = s.StartedAt,
                    LastSeenAt = s.LastSeenAt,
                    EndedAt = s.EndedAt,
                    Origin = StoredOrigin.From(s.Origin),
                }),
            ];

            // Temp then move, as everywhere else the app writes: a crash halfway
            // through leaves the previous list rather than half of this one.
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, SessionsJsonContext.Default.StoredSessionArray));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // See the note on this type: the list is disposable.
        }
    }

    /// <summary>The stored shape. Deliberately separate from the domain record.</summary>
    internal sealed class StoredSession
    {
        public string? Id { get; set; }

        public string? Folder { get; set; }

        public DateTimeOffset StartedAt { get; set; }

        public DateTimeOffset LastSeenAt { get; set; }

        public DateTimeOffset? EndedAt { get; set; }

        // Kept across restarts: the terminal outlives this app as surely as the
        // session does, and the pid and start time are re-checked before use.
        public StoredOrigin? Origin { get; set; }
    }
}
