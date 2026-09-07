using System.Text;

namespace ClaudeStatus.Security;

/// <summary>Why a pasted credential was rejected.</summary>
public enum CredentialFormatProblem
{
    /// <summary>The value looks like a Claude OAuth access token.</summary>
    None = 0,

    /// <summary>Nothing was entered.</summary>
    Empty = 1,

    /// <summary>Does not start with a recognised Anthropic token prefix.</summary>
    WrongPrefix = 2,

    /// <summary>Recognised prefix, but far too short to be a real token.</summary>
    TooShort = 3,

    /// <summary>Contains characters no Anthropic token contains - usually a copy-paste accident.</summary>
    IllegalCharacters = 4,

    /// <summary>This is a refresh token, not an access token.</summary>
    RefreshTokenNotAccessToken = 5,
}

/// <summary>
/// Shape checks for a pasted credential.
/// </summary>
/// <remarks>
/// <para>
/// Purely structural - it tells the user "that is not a token" without a network
/// round trip. Only <c>CredentialService.TestAsync</c> can say whether a
/// well-formed token actually works.
/// </para>
/// <para>
/// Everything here operates on <see cref="byte"/> spans. Nothing is copied into
/// a <see cref="string"/>, and no diagnostic ever echoes the value.
/// </para>
/// </remarks>
public static class CredentialFormat
{
    /// <summary>Prefix of a Claude Code OAuth access token.</summary>
    public static ReadOnlySpan<byte> AccessTokenPrefix => "sk-ant-oat"u8;

    /// <summary>Prefix of a Claude Code OAuth refresh token. Not what we want.</summary>
    public static ReadOnlySpan<byte> RefreshTokenPrefix => "sk-ant-ort"u8;

    /// <summary>Shortest plausible token. The real one observed was 108 bytes.</summary>
    public const int MinimumLength = 40;

    /// <summary>Longest we will accept, to bound anything a paste can produce.</summary>
    public const int MaximumLength = 4096;

    /// <summary>Checks the shape of a candidate token.</summary>
    public static CredentialFormatProblem Validate(ReadOnlySpan<byte> candidate)
    {
        if (candidate.IsEmpty)
        {
            return CredentialFormatProblem.Empty;
        }

        if (candidate.StartsWith(RefreshTokenPrefix))
        {
            // Worth its own message: pasting the refresh token is an easy mistake
            // and produces a baffling 429 rather than a clear failure.
            return CredentialFormatProblem.RefreshTokenNotAccessToken;
        }

        if (!candidate.StartsWith(AccessTokenPrefix))
        {
            return CredentialFormatProblem.WrongPrefix;
        }

        if (candidate.Length < MinimumLength || candidate.Length > MaximumLength)
        {
            return CredentialFormatProblem.TooShort;
        }

        foreach (byte b in candidate)
        {
            bool legal = b is (>= (byte)'a' and <= (byte)'z')
                or (>= (byte)'A' and <= (byte)'Z')
                or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'_';

            if (!legal)
            {
                return CredentialFormatProblem.IllegalCharacters;
            }
        }

        return CredentialFormatProblem.None;
    }

    /// <summary>
    /// The resource key for a message about this problem.
    /// </summary>
    /// <remarks>
    /// A key rather than a sentence, so Core carries no English and the same
    /// explanation can be shown in any language. The message it resolves to never
    /// contains any part of the value (<c>docs/manual.md</c> §8).
    /// </remarks>
    public static string DescribeKey(CredentialFormatProblem problem) => problem switch
    {
        CredentialFormatProblem.None => "Credential_None",
        CredentialFormatProblem.Empty => "Credential_Empty",
        CredentialFormatProblem.WrongPrefix => "Credential_WrongPrefix",
        CredentialFormatProblem.TooShort => "Credential_TooShort",
        CredentialFormatProblem.IllegalCharacters => "Credential_IllegalCharacters",
        CredentialFormatProblem.RefreshTokenNotAccessToken => "Credential_RefreshToken",
        _ => "Credential_Unknown",
    };

    /// <summary>
    /// Converts user input to bytes and clears the intermediate buffer.
    /// </summary>
    /// <remarks>
    /// The caller still holds a <see cref="string"/> from the text box; that is
    /// unavoidable with a UI control and is the one place a secret may be a
    /// string (<c>docs/manual.md</c> §8). Convert as early as possible and drop it.
    /// </remarks>
    public static byte[] ToBytes(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Encoding.UTF8.GetBytes(token.Trim());
    }
}
