using System.Globalization;
using System.Text.Json;

namespace ClaudeStatus.Sessions;

/// <summary>
/// The drop box between a hook process and the running app.
/// </summary>
/// <remarks>
/// <para>
/// A hook fires in a process of its own that lives for a few milliseconds. It has
/// to hand what it knows to the app and get out of the way. This is a directory:
/// the hook writes one small file, the app notices, reads it and deletes it.
/// </para>
/// <para>
/// <b>Why not a socket.</b> A socket needs the app listening, a port or pipe name
/// agreed in advance, and it is a local surface anything on the machine can
/// connect to - all for an event that happens a few times an hour. A directory
/// needs none of that, and an event written while the app is busy is simply still
/// there a moment later.
/// </para>
/// <para>
/// <b>One file per event, named at random.</b> Two sessions can finish a turn in
/// the same millisecond, and neither process knows about the other. Nothing
/// appends, nothing locks, nothing is read-modify-written, so there is no race to
/// get wrong.
/// </para>
/// </remarks>
public sealed class SessionSpool
{
    /// <summary>The folder name, under the app's own config directory.</summary>
    public const string DirectoryName = "session-events";

    /// <summary>
    /// How old a spooled file has to be before it is junk rather than a backlog.
    /// </summary>
    /// <remarks>
    /// Nothing writes here unless our hooks are installed, and they are removed
    /// when the app stops - but a crash can leave both behind, and then Claude
    /// Code keeps writing events nobody reads. This is what stops that becoming
    /// an ever-growing pile of tiny files.
    /// </remarks>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(12);

    private readonly string _directory;

    /// <param name="configDirectory">The app's own config directory.</param>
    public SessionSpool(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = Path.Combine(configDirectory, DirectoryName);
    }

    /// <summary>The folder being watched.</summary>
    public string Directory => _directory;

    /// <summary>Creates the folder if it is not there.</summary>
    public void Ensure() => System.IO.Directory.CreateDirectory(_directory);

    /// <summary>
    /// Writes one event, from the short-lived hook process.
    /// </summary>
    /// <remarks>
    /// Written to a temporary name and then moved into place, so the watcher on
    /// the other side can never open a file that is still being written. The
    /// timestamp in the name is for a human reading the folder; the random part
    /// is what makes it unique.
    /// </remarks>
    public void Write(SessionEvent report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Ensure();

        string name = string.Create(
            CultureInfo.InvariantCulture,
            $"{report.At.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");

        string temporary = Path.Combine(_directory, "." + name);
        string final = Path.Combine(_directory, name);

        File.WriteAllText(temporary, JsonSerializer.Serialize(new SpooledEvent
        {
            Kind = report.Kind.ToString(),
            Id = report.Id,
            Folder = report.Folder,
            At = report.At,
            Origin = StoredOrigin.From(report.Origin),
        }, SessionsJsonContext.Default.SpooledEvent));

        File.Move(temporary, final, overwrite: true);
    }

    /// <summary>
    /// Reads and removes everything waiting, oldest first.
    /// </summary>
    /// <remarks>
    /// A file that cannot be read or parsed is deleted rather than retried. It
    /// would fail identically next time, and a spool that keeps handing back the
    /// same bad file never drains.
    /// </remarks>
    public IReadOnlyList<SessionEvent> Drain(DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        var events = new List<SessionEvent>();

        foreach (string path in System.IO.Directory.GetFiles(_directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                // Left by a crash, or by hooks that outlived the app. Not a backlog.
                if (File.GetLastWriteTimeUtc(path) < (now - Stale).UtcDateTime)
                {
                    File.Delete(path);
                    continue;
                }

                string json = File.ReadAllText(path);
                File.Delete(path);

                if (JsonSerializer.Deserialize(json, SessionsJsonContext.Default.SpooledEvent) is { } spooled
                    && Enum.TryParse(spooled.Kind, ignoreCase: true, out SessionEventKind kind)
                    && !string.IsNullOrWhiteSpace(spooled.Id))
                {
                    events.Add(new SessionEvent(
                        kind, spooled.Id, spooled.Folder ?? string.Empty, spooled.At, spooled.Origin?.ToOrigin()));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                TryDelete(path);
            }
        }

        return events;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked by the writer, most likely. It will be picked up on
            // the next drain, or aged out as stale.
        }
    }

    /// <summary>The wire shape. Deliberately loose - it crosses a process boundary.</summary>
    internal sealed class SpooledEvent
    {
        public string? Kind { get; set; }

        public string? Id { get; set; }

        public string? Folder { get; set; }

        public DateTimeOffset At { get; set; }

        public StoredOrigin? Origin { get; set; }
    }
}

/// <summary>The wire and disk shape of a <see cref="SessionOrigin"/>, shared by the spool and the store.</summary>
/// <remarks>
/// Read back leniently: anything that does not describe a plausible process and
/// window becomes no origin at all, rather than a bad one handed to the code that
/// focuses windows.
/// </remarks>
internal sealed class StoredOrigin
{
    public int ProcessId { get; set; }

    public DateTimeOffset ProcessStartedAt { get; set; }

    public long Window { get; set; }

    public int Precision { get; set; }

    public static StoredOrigin? From(SessionOrigin? origin) => origin is null
        ? null
        : new StoredOrigin
        {
            ProcessId = origin.ProcessId,
            ProcessStartedAt = origin.ProcessStartedAt,
            Window = origin.Window,
            Precision = (int)origin.Precision,
        };

    /// <remarks>
    /// A window is required unless the origin only claims a process. macOS records
    /// the terminal application and has no window handle to give, so there a zero
    /// window with <see cref="SessionOriginPrecision.Process"/> is the whole answer;
    /// a <c>Console</c> or <c>Foreground</c> origin names a window and is worthless
    /// without one.
    /// </remarks>
    public SessionOrigin? ToOrigin()
        => ProcessId > 0
            && Enum.IsDefined((SessionOriginPrecision)Precision)
            && (Window != 0 || (SessionOriginPrecision)Precision == SessionOriginPrecision.Process)
            ? new SessionOrigin(ProcessId, ProcessStartedAt, Window, (SessionOriginPrecision)Precision)
            : null;
}
