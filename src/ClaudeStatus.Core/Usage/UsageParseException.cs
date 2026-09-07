namespace ClaudeStatus.Usage;

/// <summary>
/// The usage response could not be understood at all.
/// </summary>
/// <remarks>
/// Never put the response body in the message. A failing response can be an
/// error page or proxy output that echoes request headers, which would put the
/// bearer token into a log. See <c>docs/security.md</c> threat T2.
/// </remarks>
public sealed class UsageParseException : Exception
{
    public UsageParseException()
        : base("The usage response could not be parsed.")
    {
    }

    public UsageParseException(string message)
        : base(message)
    {
    }

    public UsageParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
