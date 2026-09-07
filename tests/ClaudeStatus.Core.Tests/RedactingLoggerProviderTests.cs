using Microsoft.Extensions.Logging;

namespace ClaudeStatus.Core.Tests;

public class RedactingLoggerProviderTests
{
    private const string FakeToken = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Scrubs_a_secret_that_slipped_into_a_log_message()
    {
        var sink = new CapturingProvider();
        using var provider = new RedactingLoggerProvider(sink);
        ILogger log = provider.CreateLogger("test");

        log.LogInformation("token is {Token}", FakeToken);

        sink.Messages.Should().ContainSingle();
        sink.Messages[0].Should().NotContain("sk-ant-").And.Contain(Redactor.Placeholder);
    }

    [Fact]
    public void Scrubs_a_secret_carried_in_an_exception()
    {
        var sink = new CapturingProvider();
        using var provider = new RedactingLoggerProvider(sink);
        ILogger log = provider.CreateLogger("test");

        log.LogError(new InvalidOperationException($"failed for {FakeToken}"), "fetch failed");

        sink.Messages[0].Should().NotContain("sk-ant-");
        sink.Messages[0].Should().Contain("fetch failed");
    }

    [Fact]
    public void Leaves_an_ordinary_message_readable()
    {
        var sink = new CapturingProvider();
        using var provider = new RedactingLoggerProvider(sink);

        provider.CreateLogger("test").LogWarning("Usage fetch failed ({Failure})", UsageFetchFailure.RateLimited);

        sink.Messages[0].Should().Be("Usage fetch failed (RateLimited)");
    }

    [Fact]
    public void Passes_the_enabled_check_through_to_the_inner_logger()
    {
        var sink = new CapturingProvider { MinimumLevel = LogLevel.Warning };
        using var provider = new RedactingLoggerProvider(sink);
        ILogger log = provider.CreateLogger("test");

        log.IsEnabled(LogLevel.Debug).Should().BeFalse();
        log.IsEnabled(LogLevel.Error).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_null_inner_provider()
    {
        FluentActions.Invoking(() => new RedactingLoggerProvider(null!))
            .Should().Throw<ArgumentNullException>();
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public LogLevel MinimumLevel { get; init; } = LogLevel.Trace;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.MinimumLevel;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Messages.Add(formatter(state, exception));
        }
    }
}
