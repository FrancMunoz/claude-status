namespace ClaudeStatus.Usage;

/// <summary>
/// The only abstraction that knows how usage data is obtained.
/// </summary>
/// <remarks>
/// A future official endpoint is a new implementation of this interface and
/// nothing else in the app changes.
/// </remarks>
public interface IUsageProvider
{
    /// <summary>Resource key naming this source, shown in the Info window.</summary>
    string NameKey { get; }

    /// <summary>
    /// Detail folded into <see cref="NameKey"/>'s template, when it has a placeholder.
    /// </summary>
    /// <remarks>
    /// Only the fake provider uses this, to say which scenario it is playing. The
    /// real provider's template has no placeholder, and an unused argument is
    /// simply ignored by <see cref="string.Format(IFormatProvider, string, object?)"/>.
    /// </remarks>
    object? NameArgument => null;

    /// <summary>
    /// Fetches the current usage.
    /// </summary>
    /// <exception cref="UsageFetchException">The fetch failed; inspect <see cref="UsageFetchException.Failure"/>.</exception>
    Task<UsageSnapshot> FetchAsync(CancellationToken ct);
}
