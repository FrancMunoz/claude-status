namespace ClaudeStatus.Usage;

/// <summary>
/// Remembers the last good reading across restarts.
/// </summary>
/// <remarks>
/// <para>
/// Without this, every launch shows a blank icon for up to a minute while the
/// first poll runs - and shows it indefinitely when the machine is offline. With
/// it, the tray is populated instantly and honestly marked stale.
/// </para>
/// <para>
/// <b>Contains no secret bytes.</b> Percentages and reset times are not secrets
/// (<c>docs/security.md</c> §1), and nothing else is written.
/// </para>
/// </remarks>
public interface ISnapshotCache
{
    /// <summary>Where the cache lives, for display and for deletion.</summary>
    string CacheFilePath { get; }

    /// <summary>
    /// Loads the cached reading, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// Always returns the snapshot marked stale: by definition it was fetched
    /// before the process started. Never throws - a corrupt cache is simply no
    /// cache.
    /// </remarks>
    Task<UsageSnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>Saves a reading. Failures are swallowed; a cache miss is not worth an error.</summary>
    Task SaveAsync(UsageSnapshot snapshot, CancellationToken ct = default);
}
