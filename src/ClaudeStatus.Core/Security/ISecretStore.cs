namespace ClaudeStatus.Security;

/// <summary>
/// Persists a secret using an OS-provided mechanism.
/// </summary>
/// <remarks>
/// <para>
/// Only used by the advanced "manual token" path. Under the default
/// <see cref="Config.CredentialSource.ClaudeCodeLogin"/> nothing is ever stored,
/// and no implementation of this interface is exercised.
/// </para>
/// <para>
/// Every implementation must satisfy: what is stored on one machine, under one
/// user account, is <b>unreadable</b> anywhere else. A copied config directory
/// must be inert. See <c>docs/security.md</c> §4 and threat T6.
/// </para>
/// </remarks>
public interface ISecretStore
{
    /// <summary>Resource key naming the mechanism, e.g. <c>"Store_WindowsDpapi"</c>.</summary>
    string DescriptionKey { get; }

    /// <summary>
    /// True when the secret is protected by a real OS secret service or an
    /// OS-bound key.
    /// </summary>
    /// <remarks>
    /// False only for the Linux key-file fallback, which protects against a
    /// copied config directory but not against another process running as the
    /// same user. The Config window <b>must</b> show a visible warning when this
    /// is false - see <c>docs/security.md</c> threat T11.
    /// </remarks>
    bool IsHardened { get; }

    /// <summary>Stores <paramref name="secret"/> under <paramref name="key"/>, replacing any existing value.</summary>
    /// <exception cref="SecretStoreException">The OS mechanism refused or was unavailable.</exception>
    Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct);

    /// <summary>
    /// Retrieves a stored secret, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// <b>The caller owns and must zero the returned buffer</b> - wrap it in an
    /// <see cref="AccessTokenLease"/>. A value that cannot be decrypted (a config
    /// copied from another machine, for instance) returns <c>null</c>, not an
    /// exception: an inert credential is exactly the designed behaviour.
    /// </remarks>
    Task<byte[]?> RetrieveAsync(string key, CancellationToken ct);

    /// <summary>Removes a stored secret. Deleting one that does not exist is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>The OS secret mechanism refused or was unavailable.</summary>
/// <remarks>Never carries secret material in its message.</remarks>
public sealed class SecretStoreException : Exception
{
    public SecretStoreException()
        : base("The secret store operation failed.")
    {
    }

    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
