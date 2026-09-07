namespace ClaudeStatus.Security;

/// <summary>
/// Supplies the OAuth access token for one request, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately narrow so that the default implementation can
/// hold no state at all: Iteration 1 reads Claude Code's existing login at poll
/// time and keeps nothing. See <c>docs/security.md</c> §2.
/// </para>
/// <para>
/// <b>Callers must zero the returned buffer.</b> The token is handed over as
/// <see cref="byte"/>[] rather than <see cref="string"/> precisely so it can be
/// wiped; a string would sit on the managed heap until collection, and could
/// reach a page file or a crash dump. Use
/// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>
/// in a <c>finally</c>. <see cref="AccessTokenLease"/> does this for you.
/// </para>
/// </remarks>
public interface IAccessTokenSource
{
    /// <summary>Resource key naming where this token comes from, e.g. <c>"TokenSource_ClaudeCodeLogin"</c>.</summary>
    string DescriptionKey { get; }

    /// <summary>
    /// Returns the UTF-8 bytes of the access token, or <c>null</c> when no
    /// credential is available. Never throws for "not logged in" - that is a
    /// normal state the UI reports.
    /// </summary>
    Task<byte[]?> GetAccessTokenAsync(CancellationToken ct);
}
