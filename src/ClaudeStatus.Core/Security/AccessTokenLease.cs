using System.Security.Cryptography;

namespace ClaudeStatus.Security;

/// <summary>
/// Borrows a token for the lifetime of a <c>using</c> block and zeroes it on the
/// way out, on every path including exceptions.
/// </summary>
/// <remarks>
/// This exists so that no call site has to remember to wipe. Getting a token
/// without this type is a code smell; see <c>docs/security.md</c> threat T5.
/// </remarks>
public readonly struct AccessTokenLease : IDisposable, IEquatable<AccessTokenLease>
{
    private readonly byte[]? _token;

    private AccessTokenLease(byte[]? token) => _token = token;

    /// <summary>True when a credential was actually available.</summary>
    public bool HasToken => _token is { Length: > 0 };

    /// <summary>
    /// The raw token bytes. Valid only until this lease is disposed - never
    /// store a reference to it, and never copy it into a <see cref="string"/>.
    /// </summary>
    public ReadOnlySpan<byte> Token => _token ?? ReadOnlySpan<byte>.Empty;

    /// <summary>Takes a lease over the token from <paramref name="source"/>.</summary>
    public static async Task<AccessTokenLease> AcquireAsync(IAccessTokenSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AccessTokenLease(await source.GetAccessTokenAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Wraps an already-owned buffer. The lease takes ownership and will zero it.</summary>
    public static AccessTokenLease Own(byte[]? token) => new(token);

    /// <summary>Zeroes the borrowed buffer.</summary>
    public void Dispose()
    {
        if (_token is not null)
        {
            CryptographicOperations.ZeroMemory(_token);
        }
    }

    /// <inheritdoc />
    public bool Equals(AccessTokenLease other) => ReferenceEquals(_token, other._token);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AccessTokenLease other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _token is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_token);

    public static bool operator ==(AccessTokenLease left, AccessTokenLease right) => left.Equals(right);

    public static bool operator !=(AccessTokenLease left, AccessTokenLease right) => !left.Equals(right);

    /// <summary>Never renders the token. Guards against accidental interpolation into a log.</summary>
    public override string ToString() => HasToken ? "AccessTokenLease(present)" : "AccessTokenLease(absent)";
}
