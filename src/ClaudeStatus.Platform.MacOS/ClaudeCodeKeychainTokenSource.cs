using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeStatus.Security;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// Reads Claude Code's existing login from the macOS Keychain.
/// </summary>
/// <remarks>
/// <para>
/// On macOS, Claude Code stores its credential blob in the Keychain under the
/// item <c>Claude Code-credentials</c> rather than in <c>.credentials.json</c>,
/// so the file-based source finds nothing there. Same JSON, different home - see
/// <c>docs/data-source.md</c>.
/// </para>
/// <para>
/// Reading someone else's Keychain item prompts the user for permission the
/// first time. That prompt is correct and must not be suppressed
/// (<c>docs/security.md</c> §7.1). If the user declines, this returns null and
/// the app reports that it has no credential.
/// </para>
/// <para>
/// <b>Not verifiable on the development machine.</b> Exercised on the macOS CI leg.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class ClaudeCodeKeychainTokenSource : IAccessTokenSource
{
    /// <summary>The Keychain item Claude Code files its credential under.</summary>
    public const string ClaudeCodeServiceName = "Claude Code-credentials";

    private readonly string _service;
    private readonly TimeProvider _clock;

    public ClaudeCodeKeychainTokenSource(TimeProvider? clock = null, string? service = null)
    {
        _clock = clock ?? TimeProvider.System;
        _service = service ?? ClaudeCodeServiceName;
    }

    /// <inheritdoc />
    public string DescriptionKey => "TokenSource_ClaudeCodeKeychain";

    /// <inheritdoc />
    public async Task<byte[]?> GetAccessTokenAsync(CancellationToken ct)
    {
        byte[]? blob = await KeychainSecretStore.RetrieveRawAsync(_service, ct).ConfigureAwait(false);
        if (blob is null)
        {
            return null;
        }

        try
        {
            return ClaudeCodeCredentials.ExtractAccessToken(blob, _clock.GetUtcNow());
        }
        catch (JsonException)
        {
            // Claude Code may be mid-rewrite, or the item may hold something we do
            // not understand. Either way, try again next poll.
            return null;
        }
        finally
        {
            // The blob holds the refresh token too.
            ClaudeCodeCredentials.Wipe(blob);
        }
    }
}
