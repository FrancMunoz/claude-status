using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Platform;

/// <summary>
/// Stops a second copy of the app running for the same user.
/// </summary>
/// <remarks>
/// <para>
/// Two tray icons is a visible, confusing bug, and autostart makes it easy to
/// hit: log in with the app already running from a previous session, or launch
/// it by hand after it has autostarted.
/// </para>
/// <para>
/// Implemented as an exclusively-opened lock file rather than a named
/// <see cref="System.Threading.Mutex"/>. Named mutexes behave differently across
/// the three platforms - on Unix .NET maps them onto named semaphores with their
/// own quirks - whereas an exclusive file handle behaves the same everywhere and
/// is <b>released by the OS if the process dies</b>, so a crash cannot leave the
/// app permanently unlaunchable.
/// </para>
/// <para>
/// Scoped per user by living in the user's own config directory, so two people
/// logged into the same machine each get their own instance.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>The lock file name inside the config directory.</summary>
    public const string FileName = "instance.lock";

    private readonly ILogger<SingleInstanceGuard> _log;
    private FileStream? _handle;
    private bool _disposed;

    private SingleInstanceGuard(FileStream? handle, string path, ILogger<SingleInstanceGuard> log)
    {
        _handle = handle;
        LockFilePath = path;
        _log = log;
    }

    /// <summary>True when this process owns the lock and may run.</summary>
    public bool IsPrimaryInstance => _handle is not null;

    /// <summary>Where the lock file lives.</summary>
    public string LockFilePath { get; }

    /// <summary>
    /// Tries to become the only running instance.
    /// </summary>
    /// <returns>
    /// A guard whose <see cref="IsPrimaryInstance"/> says whether this process won.
    /// A loser should exit quietly - it is not an error, it is the feature.
    /// </returns>
    public static SingleInstanceGuard Acquire(
        string configDirectory, ILogger<SingleInstanceGuard>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ILogger<SingleInstanceGuard> logger = log ?? NullLogger<SingleInstanceGuard>.Instance;

        string path = Path.Combine(configDirectory, FileName);

        try
        {
            Directory.CreateDirectory(configDirectory);

            // FileShare.None is the whole mechanism: the second process cannot
            // open the same file and therefore knows it has lost.
            var handle = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            WriteOwner(handle);
            return new SingleInstanceGuard(handle, path, logger);
        }
        catch (IOException)
        {
            // Held by another instance. Expected, not exceptional.
            logger.LogInformation("Another instance already holds {Path}; exiting.", path);
            return new SingleInstanceGuard(null, path, logger);
        }
        catch (UnauthorizedAccessException ex)
        {
            // A permissions problem must not stop the app running at all - better
            // a possible second tray icon than an app that refuses to start.
            logger.LogWarning(ex, "Could not take the single-instance lock; continuing anyway.");
            return new SingleInstanceGuard(null, path, logger)
            {
                _handle = null,
                TreatFailureAsPrimary = true,
            };
        }
    }

    /// <summary>
    /// True when the lock could not be taken for reasons unrelated to another
    /// instance, and the app should run regardless.
    /// </summary>
    public bool TreatFailureAsPrimary { get; private init; }

    /// <summary>True when this process should go on to start the UI.</summary>
    public bool ShouldRun => IsPrimaryInstance || TreatFailureAsPrimary;

    /// <summary>Records who holds the lock, purely to help someone debugging.</summary>
    private static void WriteOwner(FileStream handle)
    {
        try
        {
            byte[] pid = System.Text.Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            handle.Write(pid);
            handle.Flush();
        }
        catch (IOException)
        {
            // The lock is what matters, not the contents.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // FileOptions.DeleteOnClose removes the file as the handle closes.
        _handle?.Dispose();
        _handle = null;
        _log.LogDebug("Released the single-instance lock.");
    }
}
