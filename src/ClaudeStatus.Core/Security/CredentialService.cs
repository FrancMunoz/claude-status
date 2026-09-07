using System.Security.Cryptography;
using ClaudeStatus.Config;
using ClaudeStatus.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Security;

/// <summary>How a credential test turned out.</summary>
public enum CredentialTestOutcome
{
    /// <summary>The endpoint accepted the credential and returned usage.</summary>
    Success = 0,

    /// <summary>There is no credential to test.</summary>
    NoCredential = 1,

    /// <summary>The endpoint rejected the credential.</summary>
    Rejected = 2,

    /// <summary>The endpoint could not be reached.</summary>
    Unreachable = 3,

    /// <summary>
    /// The endpoint rate limited us, so the credential could not be judged.
    /// </summary>
    /// <remarks>
    /// Explicitly <b>not</b> a pass. An unauthenticated request also returns 429
    /// (measured 2026-09-04), so a 429 tells us nothing about validity.
    /// See <c>docs/data-source.md</c>.
    /// </remarks>
    Inconclusive = 4,
}

/// <summary>The result of a "Test" press in the Config window.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="MessageKey">
/// A resource key the UI turns into a sentence. Never contains any part of the
/// credential - it is a fixed identifier, not text derived from the token.
/// </param>
/// <param name="SessionPercent">
/// Filled in only on success, to be folded into the message. There is no other
/// way to say "works, and you are at 61 %" without Core composing prose.
/// </param>
public sealed record CredentialTestResult(
    CredentialTestOutcome Outcome, string MessageKey, double? SessionPercent = null)
{
    /// <summary>True only when the credential demonstrably works.</summary>
    public bool IsSuccess => Outcome == CredentialTestOutcome.Success;
}

/// <summary>
/// Owns the credential lifecycle: validate, store, forget, and test.
/// </summary>
/// <remarks>
/// The provider is built through a factory so a test can run against whichever
/// source the user has selected, without this type knowing anything about HTTP.
/// </remarks>
public sealed class CredentialService
{
    private readonly ISecretStore _store;
    private readonly IAccessTokenSource _claudeCodeSource;
    private readonly Func<IAccessTokenSource, IUsageProvider> _providerFactory;
    private readonly string _key;
    private readonly ILogger<CredentialService> _log;

    public CredentialService(
        ISecretStore store,
        IAccessTokenSource claudeCodeSource,
        Func<IAccessTokenSource, IUsageProvider> providerFactory,
        string key = StoredTokenSource.DefaultKey,
        ILogger<CredentialService>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _claudeCodeSource = claudeCodeSource ?? throw new ArgumentNullException(nameof(claudeCodeSource));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key;
        _log = log ?? NullLogger<CredentialService>.Instance;
    }

    /// <summary>Resource key naming the mechanism protecting a stored credential.</summary>
    public string StoreDescriptionKey => _store.DescriptionKey;

    /// <summary>
    /// False when the store is the Linux key-file fallback, which the Config
    /// window must warn about.
    /// </summary>
    public bool IsStoreHardened => _store.IsHardened;

    /// <summary>Whether a manual token is stored. Never reveals the token itself.</summary>
    public async Task<bool> HasStoredCredentialAsync(CancellationToken ct = default)
    {
        byte[]? secret = await _store.RetrieveAsync(_key, ct).ConfigureAwait(false);
        if (secret is null)
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(secret);
        return true;
    }

    /// <summary>
    /// Validates and stores a manually-entered token.
    /// </summary>
    /// <remarks>
    /// <b>Takes ownership of <paramref name="token"/> and zeroes it</b>, whether
    /// or not it turns out to be well-formed. The caller must not reuse the array.
    /// </remarks>
    public async Task<CredentialFormatProblem> StoreAsync(byte[] token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        try
        {
            CredentialFormatProblem problem = CredentialFormat.Validate(token);
            if (problem != CredentialFormatProblem.None)
            {
                // The enum carries no part of the value, so this is safe to log.
                _log.LogInformation("Rejected a pasted credential: {Problem}.", problem);
                return problem;
            }

            await _store.StoreAsync(_key, token, ct).ConfigureAwait(false);
            _log.LogInformation("Stored a credential using {Store}.", _store.DescriptionKey);
            return CredentialFormatProblem.None;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    /// <summary>Forgets any stored credential.</summary>
    public Task DeleteAsync(CancellationToken ct = default) => _store.DeleteAsync(_key, ct);

    /// <summary>Builds the token source for the configured credential source.</summary>
    public IAccessTokenSource ResolveTokenSource(CredentialSource source) => source switch
    {
        CredentialSource.ManualToken => new StoredTokenSource(_store, _key),
        _ => _claudeCodeSource,
    };

    /// <summary>
    /// Makes one real request to prove the credential works.
    /// </summary>
    /// <remarks>
    /// A 429 is reported as <see cref="CredentialTestOutcome.Inconclusive"/>,
    /// never as success. Reporting it as success would be actively misleading,
    /// because an unauthenticated request returns 429 too.
    /// </remarks>
    public async Task<CredentialTestResult> TestAsync(
        CredentialSource source, CancellationToken ct = default)
    {
        IAccessTokenSource tokenSource = ResolveTokenSource(source);
        IUsageProvider provider = _providerFactory(tokenSource);

        try
        {
            UsageSnapshot snapshot = await provider.FetchAsync(ct).ConfigureAwait(false);
            return snapshot.Session is { } session
                ? new CredentialTestResult(
                    CredentialTestOutcome.Success, "CredentialTest_Success", session.Percent)
                : new CredentialTestResult(
                    CredentialTestOutcome.Success, "CredentialTest_SuccessNoWindow");
        }
        catch (UsageFetchException ex)
        {
            return Map(ex);
        }
        catch (UsageParseException)
        {
            return new CredentialTestResult(
                CredentialTestOutcome.Unreachable, "CredentialTest_Unreadable");
        }
    }

    private static CredentialTestResult Map(UsageFetchException ex) => ex.Failure switch
    {
        UsageFetchFailure.NoCredential => new CredentialTestResult(
            CredentialTestOutcome.NoCredential, "CredentialTest_NoCredential"),

        UsageFetchFailure.Unauthorized => new CredentialTestResult(
            CredentialTestOutcome.Rejected, "CredentialTest_Rejected"),

        UsageFetchFailure.RateLimited => new CredentialTestResult(
            CredentialTestOutcome.Inconclusive, "CredentialTest_RateLimited"),

        UsageFetchFailure.ServerError => new CredentialTestResult(
            CredentialTestOutcome.Unreachable, "CredentialTest_ServerError"),

        _ => new CredentialTestResult(
            CredentialTestOutcome.Unreachable, "CredentialTest_Unreachable"),
    };
}
