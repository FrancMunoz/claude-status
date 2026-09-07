using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeStatus.Security;

/// <summary>
/// Parses the JSON blob Claude Code stores its OAuth login in.
/// </summary>
/// <remarks>
/// Shared because the blob is identical on every OS - only where it is kept
/// differs (a file on Windows and Linux, the Keychain on macOS). See
/// <c>docs/data-source.md</c>.
/// </remarks>
public static class ClaudeCodeCredentials
{
    private const string OauthPropertyName = "claudeAiOauth";
    private const string AccessTokenPropertyName = "accessToken";
    private const string ExpiresAtPropertyName = "expiresAt";

    /// <summary>
    /// Pulls <c>claudeAiOauth.accessToken</c> out of the raw UTF-8 bytes.
    /// </summary>
    /// <param name="utf8Json">The credential blob. The caller still owns and must zero it.</param>
    /// <param name="now">Clock reading used to check expiry.</param>
    /// <returns>
    /// The access token bytes, or null when absent, unreadable, or already
    /// expired. An expired token is withheld deliberately: sending it produces a
    /// 429 that reads as rate limiting, when the truthful message is
    /// "open Claude Code to refresh your login".
    /// </returns>
    /// <exception cref="JsonException">The blob is not valid JSON.</exception>
    public static byte[]? ExtractAccessToken(ReadOnlySpan<byte> utf8Json, DateTimeOffset now)
    {
        if (utf8Json.IsEmpty)
        {
            return null;
        }

        var reader = new Utf8JsonReader(utf8Json);
        using JsonDocument document = JsonDocument.ParseValue(ref reader);

        if (!document.RootElement.TryGetProperty(OauthPropertyName, out JsonElement oauth)
            || oauth.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!oauth.TryGetProperty(AccessTokenPropertyName, out JsonElement token)
            || token.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (IsExpired(oauth, now))
        {
            return null;
        }

        string? text = token.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Reads <c>expiresAt</c> (milliseconds since the epoch) and compares it to now.</summary>
    /// <remarks>
    /// A missing or unreadable expiry is treated as "not expired" - let the
    /// endpoint be the judge rather than refusing to try.
    /// </remarks>
    private static bool IsExpired(JsonElement oauth, DateTimeOffset now)
        => oauth.TryGetProperty(ExpiresAtPropertyName, out JsonElement expiresAt)
        && expiresAt.ValueKind == JsonValueKind.Number
        && expiresAt.TryGetInt64(out long milliseconds)
        && DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) <= now;

    /// <summary>Zeroes a credential blob. A convenience so no call site forgets.</summary>
    public static void Wipe(byte[]? blob)
    {
        if (blob is not null)
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }
}
