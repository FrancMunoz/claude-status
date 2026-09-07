using System.Text.Json;

namespace ClaudeStatus.Security;

/// <summary>
/// Reads Claude Code's existing login from <c>.credentials.json</c> at poll time.
/// </summary>
/// <remarks>
/// <para>
/// This is the Iteration 1 default (Option A in <c>docs/data-source.md</c>): we
/// store nothing, hold nothing, and never refresh. About an hour after Claude
/// Code last refreshed, the token expires and our data goes stale until the user
/// opens Claude Code again. That is the honest, intended behaviour.
/// </para>
/// <para>
/// Covers Windows and Linux, where the credential is a file. macOS keeps it in
/// the Keychain, so <c>ClaudeStatus.Platform.MacOS</c> supplies its own
/// implementation of <see cref="IAccessTokenSource"/>.
/// </para>
/// <para>
/// <b>We only ever read.</b> Writing back would mean participating in refresh
/// token rotation, and getting that wrong breaks the user's Claude Code login
/// (threat T8).
/// </para>
/// </remarks>
public sealed class ClaudeCodeFileTokenSource : IAccessTokenSource
{
    /// <summary>Overrides the whole config directory when set, as Claude Code itself honours.</summary>
    public const string ConfigDirEnvironmentVariable = "CLAUDE_CONFIG_DIR";

    private const string CredentialsFileName = ".credentials.json";

    private readonly Func<string?> _credentialsPathResolver;
    private readonly TimeProvider _clock;

    public ClaudeCodeFileTokenSource(string? credentialsPath = null, TimeProvider? clock = null)
    {
        _credentialsPathResolver = credentialsPath is null
            ? DefaultCredentialsPath
            : () => credentialsPath;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string DescriptionKey => "TokenSource_ClaudeCodeLogin";

    /// <summary>The path being read, for display in Config. Null when it cannot be determined.</summary>
    public string? CredentialsPath => _credentialsPathResolver();

    /// <summary>True when the credentials file exists. Does not imply the token is still valid.</summary>
    public bool IsAvailable => CredentialsPath is { } path && File.Exists(path);

    /// <summary>The default location, honouring <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public static string? DefaultCredentialsPath()
    {
        string? configDir = Environment.GetEnvironmentVariable(ConfigDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return Path.Combine(configDir, CredentialsFileName);
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude", CredentialsFileName);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAccessTokenAsync(CancellationToken ct)
    {
        string? path = _credentialsPathResolver();
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        byte[] blob;
        try
        {
            blob = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Claude Code may be rewriting the file, or we may lack permission.
            // Either way this is "no credential right now", not a crash.
            return null;
        }

        try
        {
            return ClaudeCodeCredentials.ExtractAccessToken(blob, _clock.GetUtcNow());
        }
        catch (JsonException)
        {
            // A half-written file during Claude Code's own refresh. Try again next poll.
            return null;
        }
        finally
        {
            // The file contents include the refresh token, which is the more
            // valuable of the two. Wipe the whole buffer, not just the part we read.
            ClaudeCodeCredentials.Wipe(blob);
        }
    }
}
