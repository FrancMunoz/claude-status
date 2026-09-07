namespace ClaudeStatus.Config;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>.
/// </summary>
/// <remarks>
/// Loading never throws for a missing, empty, or corrupt file - it returns
/// defaults. Losing settings is an annoyance; refusing to start because a JSON
/// file has a stray comma is a bug.
/// </remarks>
public interface IConfigStore
{
    /// <summary>Full path of the settings file, for display in Config and Info.</summary>
    string SettingsFilePath { get; }

    /// <summary>True when a settings file already exists, i.e. this is not a first run.</summary>
    bool Exists { get; }

    /// <summary>Loads settings, falling back to defaults on any problem.</summary>
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    /// <summary>Saves settings atomically.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
