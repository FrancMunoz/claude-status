using Microsoft.Extensions.Logging;

namespace ClaudeStatus.Logging;

/// <summary>
/// Wraps another <see cref="ILoggerProvider"/> and runs every message through
/// <see cref="Redactor"/> before it is written.
/// </summary>
/// <remarks>
/// Register this as the outermost provider so nothing reaches a sink unscrubbed.
/// It redacts the formatted message and the exception text; scopes pass through
/// unchanged, so never put a secret in a scope.
/// </remarks>
public sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider
{
    private readonly ILoggerProvider _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(_inner.CreateLogger(categoryName));

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => inner.BeginScope(state);

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            // The message is materialised here and scrubbed before it can reach a
            // sink. The original exception is passed through unformatted only
            // after its text has been folded into the message.
            string message = Redactor.Redact(formatter(state, exception));
            if (exception is not null)
            {
                message = string.Concat(message, Environment.NewLine, Redactor.Redact(exception.ToString()));
            }

            inner.Log(
                logLevel,
                eventId,
                message,
                exception: null,
                formatter: static (text, _) => text);
        }
    }
}
