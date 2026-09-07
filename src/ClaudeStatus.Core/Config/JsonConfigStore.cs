using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Config;

/// <summary>
/// Stores settings as JSON in the per-OS config directory.
/// </summary>
/// <remarks>
/// <para>
/// The directory is supplied rather than discovered, so this type stays free of
/// platform branching; <c>IPlatformInfo</c> resolves it at composition time.
/// </para>
/// <para>
/// Serialization goes through <see cref="ClaudeStatusJsonContext"/>, never the
/// reflection-based overloads: those work in a normal build and are silently
/// removed by trimming and AOT.
/// </para>
/// </remarks>
public sealed class JsonConfigStore : IConfigStore
{
    /// <summary>The settings file name inside the config directory.</summary>
    public const string FileName = "settings.json";

    private readonly string _directory;
    private readonly ILogger<JsonConfigStore> _log;

    public JsonConfigStore(string configDirectory, ILogger<JsonConfigStore>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = configDirectory;
        _log = log ?? NullLogger<JsonConfigStore>.Instance;
        SettingsFilePath = Path.Combine(_directory, FileName);
    }

    /// <inheritdoc />
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public bool Exists => File.Exists(SettingsFilePath);

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(SettingsFilePath))
        {
            _log.LogInformation("No settings file yet; using defaults. This is a first run.");
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(SettingsFilePath);
            AppSettings? loaded = await JsonSerializer
                .DeserializeAsync(stream, ClaudeStatusJsonContext.Default.AppSettings, ct)
                .ConfigureAwait(false);

            return (loaded ?? new AppSettings()).Normalized();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The path is fine to log: it holds no secret, and neither does the file.
            _log.LogWarning(ex, "Could not read {Path}; falling back to defaults.", SettingsFilePath);
            return new AppSettings();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes to a temporary file and renames over the target, so a crash or a
    /// full disk mid-write leaves the previous settings intact rather than a
    /// truncated file. The temporary file is removed if anything goes wrong -
    /// an earlier version left 0-byte <c>.tmp</c> files behind on failure.
    /// </remarks>
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(_directory);

        string tempPath = SettingsFilePath + ".tmp";
        try
        {
            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, settings.Normalized(), ClaudeStatusJsonContext.Default.AppSettings, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
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
            // Cleaning up is best effort; the original failure is what matters.
        }
    }
}
