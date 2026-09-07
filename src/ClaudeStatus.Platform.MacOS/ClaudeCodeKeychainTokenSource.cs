using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeStatus.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// Reading someone else's Keychain item prompts the user for permission. That
/// prompt is correct and must not be suppressed (<c>docs/security.md</c> §7.1) -
/// but it must also be asked as few times as it can honestly be asked, which is
/// what separates this source from the file one. A file can be re-read on every
/// poll for nothing; a Keychain item cannot, so this holds on to what it read
/// (§7.6) and only goes back when the token it holds has run out.
/// </para>
/// <para>
/// If the user declines, this returns null and the app reports that it has no
/// credential - and then stops asking. A refusal is an answer, and re-asking on
/// the next poll would turn one prompt the user said no to into one prompt a
/// minute. <see cref="Forget"/>, which the Config window's Test button reaches,
/// is how the user takes it back.
/// </para>
/// <para>
/// <b>Not verifiable on the development machine.</b> Exercised on the macOS CI leg.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class ClaudeCodeKeychainTokenSource : ICachingAccessTokenSource, IDisposable
{
    /// <summary>The Keychain read this source is built over.</summary>
    /// <remarks>
    /// A seam, so the holding and refusing above can be tested without a Keychain
    /// and without a prompt nobody is there to answer. Everything worth getting
    /// wrong here - handing back a copy, zeroing what is replaced, never asking
    /// twice after a no - is decided in this class, not in Security.framework.
    /// </remarks>
    internal delegate Task<KeychainReadResult> KeychainReader(string service, CancellationToken ct);

    /// <summary>The Keychain item Claude Code files its credential under.</summary>
    public const string ClaudeCodeServiceName = "Claude Code-credentials";

    /// <summary>How long before a token's own expiry to stop trusting it.</summary>
    /// <remarks>
    /// A token that expires during the request it was fetched for is worse than
    /// one fetched a moment later: the reading comes back as a rejection rather
    /// than a number. A minute covers the request and the clock skew between this
    /// machine and Anthropic's, and costs one early Keychain read per hour.
    /// </remarks>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a token with no stated expiry is held.
    /// </summary>
    /// <remarks>
    /// Claude Code always writes <c>expiresAt</c>, so this is the path for a blob
    /// shaped differently than expected rather than one seen in practice. Holding
    /// it briefly rather than not at all is the choice that keeps an unexpected
    /// shape from costing a prompt per poll; keeping it short is what limits how
    /// long an already-dead token can go on being offered.
    /// </remarks>
    private static readonly TimeSpan UnknownExpiryLifetime = TimeSpan.FromMinutes(5);

    private readonly KeychainReader _read;
    private readonly string _service;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaudeCodeKeychainTokenSource> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The token last read, held until <see cref="_holdUntil"/>.</summary>
    /// <remarks>
    /// A <c>byte[]</c> and never a <c>string</c>, and zeroed whenever it is
    /// replaced or dropped: T5 applies here exactly as it does everywhere else,
    /// and the only thing this cache changes is how long the window is.
    /// </remarks>
    private byte[]? _held;

    private DateTimeOffset _holdUntil;

    /// <summary>Set once the user has refused, to stop asking again.</summary>
    private bool _refused;

    private bool _disposed;

    public ClaudeCodeKeychainTokenSource(
        TimeProvider? clock = null,
        string? service = null,
        ILogger<ClaudeCodeKeychainTokenSource>? log = null)
        : this(KeychainSecretStore.RetrieveRawAsync, clock, service, log)
    {
    }

    internal ClaudeCodeKeychainTokenSource(
        KeychainReader read,
        TimeProvider? clock = null,
        string? service = null,
        ILogger<ClaudeCodeKeychainTokenSource>? log = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _clock = clock ?? TimeProvider.System;
        _service = service ?? ClaudeCodeServiceName;
        _log = log ?? NullLogger<ClaudeCodeKeychainTokenSource>.Instance;
    }

    /// <inheritdoc />
    public string DescriptionKey => "TokenSource_ClaudeCodeKeychain";

    /// <inheritdoc />
    public async Task<byte[]?> GetAccessTokenAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();

            // The whole point: a poll that already has a usable token never
            // reaches Security.framework, so it can never raise a prompt.
            if (_held is not null && now < _holdUntil)
            {
                return _held.AsSpan().ToArray();
            }

            Drop();

            if (_refused)
            {
                return null;
            }

            KeychainReadResult read = await _read(_service, ct).ConfigureAwait(false);

            switch (read.Outcome)
            {
                case KeychainReadOutcome.Denied:
                    _refused = true;
                    _log.LogInformation(
                        "Keychain access to Claude Code's login was declined. Not asking again "
                        + "until Config tests the credential, or the app restarts.");
                    return null;

                case KeychainReadOutcome.NotFound:
                case KeychainReadOutcome.Unavailable:
                    // Nobody was asked anything, and Claude Code may log in at any
                    // moment. Worth another look on the next poll.
                    return null;
            }

            byte[] blob = read.Secret!;
            try
            {
                byte[]? token = ClaudeCodeCredentials.ExtractAccessToken(
                    blob, now, out DateTimeOffset? expiresAt);

                if (token is null)
                {
                    return null;
                }

                Hold(token, now, expiresAt);
                return token.AsSpan().ToArray();
            }
            catch (JsonException)
            {
                // Claude Code may be mid-rewrite, or the item may hold something we
                // do not understand. Either way, try again next poll.
                return null;
            }
            finally
            {
                // The blob holds the refresh token too.
                ClaudeCodeCredentials.Wipe(blob);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Forget()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            Drop();
            _refused = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Zeroes and releases the held token.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Drop();
        _gate.Dispose();
    }

    /// <summary>Takes a copy to hold, and works out how long it is good for.</summary>
    private void Hold(byte[] token, DateTimeOffset now, DateTimeOffset? expiresAt)
    {
        DateTimeOffset until = expiresAt is { } deadline
            ? deadline - ExpiryMargin
            : now + UnknownExpiryLifetime;

        // A token already inside the margin is still worth returning once - the
        // endpoint, not this class, decides whether it works - but holding it
        // would mean handing back something known to be past its deadline.
        if (until <= now)
        {
            return;
        }

        _held = token.AsSpan().ToArray();
        _holdUntil = until;
    }

    /// <summary>Zeroes whatever is held and forgets the deadline.</summary>
    private void Drop()
    {
        if (_held is not null)
        {
            CryptographicOperations.ZeroMemory(_held);
            _held = null;
        }

        _holdUntil = default;
    }
}
