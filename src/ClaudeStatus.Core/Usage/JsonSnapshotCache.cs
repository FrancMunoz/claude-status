using System.Text.Json;
using ClaudeStatus.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Usage;

/// <summary>
/// Caches the last reading as JSON beside the settings file.
/// </summary>
/// <remarks>
/// Serialization goes through <see cref="ClaudeStatusJsonContext"/> and the flat
/// <see cref="CachedSnapshot"/> DTO, never the reflection-based overloads: those
/// work in a normal build and are silently removed by trimming and AOT.
/// </remarks>
public sealed class JsonSnapshotCache : ISnapshotCache
{
    /// <summary>The cache file name inside the config directory.</summary>
    public const string FileName = "last-snapshot.json";

    /// <summary>
    /// How old a cached reading may be before it is ignored.
    /// </summary>
    /// <remarks>
    /// A week-old percentage is not information, it is a misleading number in a
    /// tray. Past this the app shows "no data" instead.
    /// </remarks>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(2);

    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly ILogger<JsonSnapshotCache> _log;

    public JsonSnapshotCache(
        string configDirectory, TimeProvider? clock = null, ILogger<JsonSnapshotCache>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = configDirectory;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<JsonSnapshotCache>.Instance;
        CacheFilePath = Path.Combine(_directory, FileName);
    }

    /// <inheritdoc />
    public string CacheFilePath { get; }

    /// <inheritdoc />
    public async Task<UsageSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(CacheFilePath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(CacheFilePath);
            CachedSnapshot? cached = await JsonSerializer
                .DeserializeAsync(stream, ClaudeStatusJsonContext.Default.CachedSnapshot, ct)
                .ConfigureAwait(false);

            if (cached is null)
            {
                return null;
            }

            UsageSnapshot snapshot = cached.ToSnapshot();

            if (snapshot.Age(_clock.GetUtcNow()) > MaximumAge)
            {
                _log.LogInformation("Ignoring a cached reading older than {MaximumAge}.", MaximumAge);
                return null;
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the snapshot cache; starting without one.");
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Swallows every failure, because a missing cache costs one blank icon on the
    /// next launch and is never worth surfacing. The catch is deliberately broad:
    /// an earlier version caught only <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/>, so when trimming removed the
    /// serializer the resulting exception escaped into a fire-and-forget task and
    /// the cache stopped working with nothing logged at all.
    /// </remarks>
    public async Task SaveAsync(UsageSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string tempPath = CacheFilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_directory);

            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer
                    .SerializeAsync(
                        stream,
                        CachedSnapshot.From(snapshot),
                        ClaudeStatusJsonContext.Default.CachedSnapshot,
                        ct)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, CacheFilePath, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            _log.LogWarning(ex, "Could not write the snapshot cache; the next launch will start blank.");
        }
    }

    /// <summary>Removes a leftover temporary file, ignoring any failure to do so.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleaning up is best effort.
        }
    }
}
