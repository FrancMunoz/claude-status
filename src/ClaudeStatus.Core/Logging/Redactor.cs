using System.Text.RegularExpressions;

namespace ClaudeStatus.Logging;

/// <summary>
/// Scrubs credential-shaped text out of anything on its way to a log.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the second line of defence, not the first.</b> The rule in
/// <c>docs/manual.md</c> §8 is that a secret never reaches a log message in the first
/// place. This exists to catch the mistake, not to license it.
/// </para>
/// <para>
/// Patterns are deliberately broad. A false positive costs a redacted word in a
/// log file; a false negative costs a leaked token.
/// </para>
/// </remarks>
public static partial class Redactor
{
    /// <summary>What replaces a match.</summary>
    public const string Placeholder = "[REDACTED]";

    /// <summary>Anthropic keys and OAuth tokens: <c>sk-ant-…</c>, including oat01/ort01 forms.</summary>
    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{4,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnthropicSecret();

    /// <summary>An <c>Authorization: Bearer …</c> value, keeping the scheme so the log still reads sensibly.</summary>
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-~+/]{8,}=*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerToken();

    /// <summary>Anything JWT-shaped: three base64url segments separated by dots.</summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonWebToken();

    /// <summary>A JSON field whose name suggests a secret, e.g. <c>"accessToken": "…"</c>.</summary>
    [GeneratedRegex(
        @"(?i)""(access_?token|refresh_?token|api_?key|secret|password|authorization)""\s*:\s*""[^""]*""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretJsonField();

    /// <summary>
    /// Account identifiers from response headers. Not credentials, but they
    /// identify the user's org and should not sit in a log file (threat T10).
    /// </summary>
    [GeneratedRegex(
        @"(?i)\b(anthropic-(?:organization|workspace)-id)\s*[:=]\s*\S+",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AccountIdentifier();

    /// <summary>Replaces every credential-shaped run in <paramref name="text"/>.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            string result = SecretJsonField().Replace(text, match => RedactJsonFieldValue(match.Value));
            result = AnthropicSecret().Replace(result, Placeholder);
            result = JsonWebToken().Replace(result, Placeholder);
            result = BearerToken().Replace(result, "Bearer " + Placeholder);
            result = AccountIdentifier().Replace(result, match => RedactHeaderValue(match.Value));
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input. Drop the whole message rather than risk emitting it.
            return Placeholder;
        }
    }

    /// <summary>True when the text still looks like it contains a secret. Used by tests.</summary>
    public static bool LooksRedacted(string? text)
        => string.IsNullOrEmpty(text)
        || (!AnthropicSecret().IsMatch(text) && !JsonWebToken().IsMatch(text));

    /// <summary>Keeps the field name, replaces the value.</summary>
    private static string RedactJsonFieldValue(string match)
    {
        int colon = match.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? Placeholder : string.Concat(match.AsSpan(0, colon + 1), " \"", Placeholder, "\"");
    }

    /// <summary>Keeps the header name, replaces the value.</summary>
    private static string RedactHeaderValue(string match)
    {
        int separator = match.IndexOfAny([':', '=']);
        return separator < 0 ? Placeholder : string.Concat(match.AsSpan(0, separator + 1), " ", Placeholder);
    }
}
