namespace ClaudeStatus.Usage;

/// <summary>Why a fetch failed, in terms the UI can act on.</summary>
public enum UsageFetchFailure
{
    /// <summary>Network unreachable, DNS failure, TLS failure, or timeout.</summary>
    Network = 0,

    /// <summary>No credential was available to send.</summary>
    NoCredential = 1,

    /// <summary>The credential was rejected (401/403). The user must log in again.</summary>
    Unauthorized = 2,

    /// <summary>
    /// The source asked us to slow down (429).
    /// </summary>
    /// <remarks>
    /// Careful: an unauthenticated request also returns 429, not 401 (measured
    /// 2026-09-04). This value only means "the server said 429" - it does not
    /// prove the credential is valid. See <c>docs/data-source.md</c>.
    /// </remarks>
    RateLimited = 3,

    /// <summary>The source returned something we could not parse.</summary>
    Unreadable = 4,

    /// <summary>A server-side error (5xx).</summary>
    ServerError = 5,
}

/// <summary>A fetch attempt failed. Carries no response body, by design.</summary>
public sealed class UsageFetchException : Exception
{
    public UsageFetchException(UsageFetchFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
        => Failure = failure;

    public UsageFetchException()
        : this(UsageFetchFailure.Network, "The usage request failed.")
    {
    }

    public UsageFetchException(string message)
        : this(UsageFetchFailure.Network, message)
    {
    }

    public UsageFetchException(string message, Exception innerException)
        : this(UsageFetchFailure.Network, message, innerException)
    {
    }

    /// <summary>What went wrong, in a form the UI can branch on.</summary>
    public UsageFetchFailure Failure { get; }

    /// <summary>How long the server asked us to wait, when it said so (<c>Retry-After</c>).</summary>
    public TimeSpan? RetryAfter { get; init; }
}
