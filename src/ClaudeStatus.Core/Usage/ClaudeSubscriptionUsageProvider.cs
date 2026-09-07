using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClaudeStatus.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeStatus.Usage;

/// <summary>
/// Reads usage from Claude's undocumented subscription endpoint.
/// </summary>
/// <remarks>
/// See <c>docs/data-source.md</c>. This type owns the HTTP call and the error
/// classification only; the response shape is handled by
/// <see cref="UsageResponseParser"/> so that parsing is testable without a network.
/// </remarks>
public sealed class ClaudeSubscriptionUsageProvider : IUsageProvider
{
    /// <summary>The one host this app is ever allowed to talk to (<c>docs/security.md</c> §5).</summary>
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    /// <summary>Required, or the endpoint refuses the OAuth token.</summary>
    private const string BetaHeaderName = "anthropic-beta";
    private const string BetaHeaderValue = "oauth-2025-04-20";

    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokenSource;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaudeSubscriptionUsageProvider> _log;

    public ClaudeSubscriptionUsageProvider(
        HttpClient http,
        IAccessTokenSource tokenSource,
        TimeProvider? clock = null,
        ILogger<ClaudeSubscriptionUsageProvider>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ClaudeSubscriptionUsageProvider>.Instance;
    }

    /// <inheritdoc />
    public string NameKey => "Provider_ClaudeSubscription";

    /// <inheritdoc />
    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        using AccessTokenLease lease = await AccessTokenLease.AcquireAsync(_tokenSource, ct).ConfigureAwait(false);
        if (!lease.HasToken)
        {
            throw new UsageFetchException(
                UsageFetchFailure.NoCredential,
                "No Claude credential is available. Sign in with Claude Code, or enter a token in Config.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);

        // ACCEPTED WEAKNESS (docs/security.md §7.5): HttpClient's header API takes
        // a string, so the token must become one here. There is no byte-oriented
        // path through HttpRequestHeaders. We keep the string's life as short as
        // possible - it is created inline, handed straight to the header, and never
        // stored in a local, field, or log. The byte[] lease is still zeroed on exit.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", Encoding.UTF8.GetString(lease.Token));
        request.Headers.TryAddWithoutValidation(BetaHeaderName, BetaHeaderValue);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UsageFetchException(UsageFetchFailure.Network, "Could not reach the usage endpoint.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // A TaskCanceledException that is not our cancellation is the client timeout.
            throw new UsageFetchException(UsageFetchFailure.Network, "The usage request timed out.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Classify(response);
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return UsageResponseParser.Parse(body, _clock.GetUtcNow());
        }
    }

    /// <summary>Maps a failure response onto something the UI can act on.</summary>
    /// <remarks>
    /// The 429 case is subtle. An <i>unauthenticated</i> request also returns 429
    /// (measured 2026-09-04), so a 429 does not prove the credential is good, and
    /// it does not prove we are polling too fast either. We report it as
    /// <see cref="UsageFetchFailure.RateLimited"/> and let the monitor back off,
    /// because backing off is safe under either reading.
    /// </remarks>
    private UsageFetchException Classify(HttpResponseMessage response)
    {
        TimeSpan? retryAfter = ReadRetryAfter(response);

        // Status code only - never the body, which may echo request headers.
        _log.LogWarning(
            "Usage request failed with {StatusCode}. Retry-After: {RetryAfter}.",
            (int)response.StatusCode,
            retryAfter?.ToString() ?? "none");

        (UsageFetchFailure failure, string message) = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (
                UsageFetchFailure.Unauthorized,
                "The Claude credential was rejected. Open Claude Code to refresh your login."),

            HttpStatusCode.TooManyRequests => (
                UsageFetchFailure.RateLimited,
                "The usage endpoint is rate limiting us. Showing the last known values."),

            >= HttpStatusCode.InternalServerError => (
                UsageFetchFailure.ServerError,
                "The usage endpoint returned a server error."),

            _ => (
                UsageFetchFailure.Network,
                $"The usage endpoint returned {(int)response.StatusCode}."),
        };

        return new UsageFetchException(failure, message) { RetryAfter = retryAfter };
    }

    /// <summary>
    /// Reads <c>Retry-After</c>, which may be delta-seconds or an HTTP date.
    /// </summary>
    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (header.Date is { } date)
        {
            TimeSpan until = date - _clock.GetUtcNow();
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }

        return null;
    }
}
