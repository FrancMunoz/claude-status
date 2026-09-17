using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeStatus.Sessions;

/// <summary>What the last attempt to sync our hooks did.</summary>
public enum HookSyncOutcome
{
    /// <summary>The file already said what it should. Nothing was written.</summary>
    Unchanged = 0,

    /// <summary>Our hooks were added or repointed.</summary>
    Installed = 1,

    /// <summary>Our hooks were taken out.</summary>
    Removed = 2,

    /// <summary>Nothing was done, and the reason is in <see cref="HookSyncResult.MessageKey"/>.</summary>
    Failed = 3,
}

/// <summary>The outcome of a sync, with a resource key when it went wrong.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="MessageKey">
/// A resource key for the failure, or null. A key rather than a sentence: this is
/// Core, and Core does not write English (CLAUDE.md §5b).
/// </param>
/// <param name="Path">The settings file this was about, for display.</param>
public sealed record HookSyncResult(HookSyncOutcome Outcome, string? MessageKey, string? Path)
{
    /// <summary>Whether the app's hooks are now in place.</summary>
    public bool Succeeded => Outcome != HookSyncOutcome.Failed;
}

/// <summary>
/// Keeps ClaudeStatus's hooks in Claude Code's settings file.
/// </summary>
/// <remarks>
/// An interface so the app can be tested, and run, without a Claude Code install
/// to write to.
/// </remarks>
public interface IHookManager
{
    /// <summary>The settings file being managed, for display. Null when it cannot be located.</summary>
    string? SettingsPath { get; }

    /// <summary>Whether our hooks are currently installed.</summary>
    Task<bool> IsInstalledAsync(CancellationToken ct = default);

    /// <summary>Adds or removes our hooks so the file matches <paramref name="enabled"/>.</summary>
    Task<HookSyncResult> SyncAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>
/// Reads, edits and rewrites <c>~/.claude/settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This writes a file another application depends on to start.</b> Everything
/// here is arranged around not breaking it:
/// </para>
/// <list type="bullet">
/// <item>
/// The document is edited as a <see cref="JsonNode"/> DOM, so keys this version
/// has never heard of come back out exactly as they went in. Deserializing into
/// a model of our own would silently drop everything the model omits.
/// </item>
/// <item>
/// Unparseable input is left alone and reported. A settings file we cannot read
/// is one somebody is in the middle of editing, or one written by a newer Claude
/// Code; either way, overwriting it with our idea of its contents would be the
/// worst thing we could do.
/// </item>
/// <item>
/// The write goes to a temporary file in the same directory and is then moved
/// over the original, so a crash or a full disk leaves the old file intact
/// rather than a half-written one.
/// </item>
/// <item>
/// Nothing is written when nothing changed, so an app that starts twice a day
/// does not rewrite a file it has no business touching.
/// </item>
/// </list>
/// </remarks>
public sealed class ClaudeCodeHookManager : IHookManager
{
    /// <summary>Honoured the same way <c>ClaudeCodeFileTokenSource</c> honours it.</summary>
    private const string ConfigDirEnvironmentVariable = "CLAUDE_CONFIG_DIR";

    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Func<string?> _pathResolver;
    private readonly Func<string> _executableResolver;

    /// <param name="executableResolver">
    /// What the hooks should invoke. A function rather than a value because the
    /// path changes when the app updates, and the answer has to be the one that
    /// is true at the moment we write.
    /// </param>
    /// <param name="pathResolver">The settings file. Defaults to Claude Code's own location.</param>
    public ClaudeCodeHookManager(Func<string> executableResolver, Func<string?>? pathResolver = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _pathResolver = pathResolver ?? DefaultSettingsPath;
    }

    /// <inheritdoc />
    public string? SettingsPath => _pathResolver();

    /// <summary>Claude Code's settings file, honouring <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public static string? DefaultSettingsPath()
    {
        string? configDir = Environment.GetEnvironmentVariable(ConfigDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return Path.Combine(configDir, SettingsFileName);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude", SettingsFileName);
    }

    /// <inheritdoc />
    public async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        string? path = _pathResolver();
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return ClaudeCodeHooks.IsInstalled(await ReadAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<HookSyncResult> SyncAsync(bool enabled, CancellationToken ct = default)
    {
        string? path = _pathResolver();
        if (path is null)
        {
            return new HookSyncResult(HookSyncOutcome.Failed, "Hooks_Error_NoSettingsPath", null);
        }

        try
        {
            JsonObject settings;
            if (File.Exists(path))
            {
                JsonObject? parsed = await ReadAsync(path, ct).ConfigureAwait(false);
                if (parsed is null)
                {
                    // Valid JSON, but not an object - an array, a bare string.
                    // Not something we can safely add a key to.
                    return new HookSyncResult(HookSyncOutcome.Failed, "Hooks_Error_Unreadable", path);
                }

                settings = parsed;
            }
            else if (!enabled)
            {
                // Nothing to remove, and creating a settings file purely to say
                // it has no hooks of ours would be rude.
                return new HookSyncResult(HookSyncOutcome.Unchanged, null, path);
            }
            else
            {
                settings = [];
            }

            if (!ClaudeCodeHooks.Reconcile(settings, enabled, _executableResolver()))
            {
                return new HookSyncResult(HookSyncOutcome.Unchanged, null, path);
            }

            await WriteAsync(path, settings, ct).ConfigureAwait(false);

            return new HookSyncResult(
                enabled ? HookSyncOutcome.Installed : HookSyncOutcome.Removed, null, path);
        }
        catch (JsonException)
        {
            // Half-edited by hand, or written by something newer. Leaving it
            // alone is the only safe answer.
            return new HookSyncResult(HookSyncOutcome.Failed, "Hooks_Error_Unreadable", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HookSyncResult(HookSyncOutcome.Failed, "Hooks_Error_Write", path);
        }
    }

    private static async Task<JsonObject?> ReadAsync(string path, CancellationToken ct)
    {
        string text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject;
    }

    /// <summary>Writes through a temporary file so a failure cannot truncate the original.</summary>
    private static async Task WriteAsync(string path, JsonObject settings, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);

        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporary, settings.ToJsonString(WriteOptions), ct)
                .ConfigureAwait(false);

            // Move, not copy: the replacement is atomic on every platform we
            // ship, so no reader ever sees a partial file.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // A leftover temp file is untidy, not harmful, and throwing
                    // from here would replace a real error with this one.
                }
            }
        }
    }
}
