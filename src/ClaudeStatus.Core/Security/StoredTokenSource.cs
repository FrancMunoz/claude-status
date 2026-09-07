namespace ClaudeStatus.Security;

/// <summary>
/// Supplies the manually-entered token from an <see cref="ISecretStore"/>.
/// </summary>
/// <remarks>
/// The advanced fallback, for users without Claude Code installed. This is the
/// only path where ClaudeStatus holds a secret of its own.
/// </remarks>
public sealed class StoredTokenSource : IAccessTokenSource
{
    /// <summary>The key the manual access token is filed under.</summary>
    public const string DefaultKey = "claudestatus.access-token";

    private readonly ISecretStore _store;
    private readonly string _key;

    public StoredTokenSource(ISecretStore store, string key = DefaultKey)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
    }

    /// <inheritdoc />
    public string DescriptionKey => "TokenSource_ManualToken";

    /// <inheritdoc />
    public Task<byte[]?> GetAccessTokenAsync(CancellationToken ct) => _store.RetrieveAsync(_key, ct);
}
