using Microsoft.Extensions.Logging;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The logging pipeline end to end, on the real filesystem.
/// </summary>
/// <remarks>
/// Every token here is synthetic. The point of these tests is threat T1: a
/// secret must not reach a log file even when someone carelessly logs one.
/// </remarks>
public class LoggingPipelineTests : IDisposable
{
    private const string FakeAccessToken = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FakeRefreshToken = "sk-ant-ort01-BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-logs", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Builds the pipeline exactly as the app does: redactor outermost.</summary>
    private (ILogger Logger, RollingFileLoggerProvider File, IDisposable Root) Build(
        LogLevel minimum = LogLevel.Information)
    {
        var file = new RollingFileLoggerProvider(_directory, minimum);
        var redacting = new RedactingLoggerProvider(file);
        return (redacting.CreateLogger("ClaudeStatus.Tests.Subject"), file, redacting);
    }

    private static string ReadLog(RollingFileLoggerProvider file)
        => File.Exists(file.LogFilePath) ? File.ReadAllText(file.LogFilePath) : string.Empty;

    [Fact]
    public void Writes_a_line_to_the_configured_file()
    {
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            logger.LogInformation("hello from the tray");
        }

        ReadLog(file).Should().Contain("hello from the tray");
    }

    [Fact]
    public void A_secret_logged_by_mistake_never_reaches_the_file()
    {
        // The whole reason the redactor is registered globally (threat T1).
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            logger.LogWarning("token was {Token}", FakeAccessToken);
            logger.LogWarning("refresh was {Token}", FakeRefreshToken);
            logger.LogWarning("header: Authorization: Bearer {Token}", FakeAccessToken);
        }

        string contents = ReadLog(file);

        contents.Should().NotContain("sk-ant-");
        contents.Should().Contain(Redactor.Placeholder);
        Redactor.LooksRedacted(contents).Should().BeTrue();
    }

    [Fact]
    public void A_secret_inside_an_exception_never_reaches_the_file()
    {
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            logger.LogError(new InvalidOperationException($"failed for {FakeAccessToken}"), "fetch failed");
        }

        string contents = ReadLog(file);

        contents.Should().NotContain("sk-ant-");
        contents.Should().Contain("fetch failed");
    }

    [Fact]
    public void Account_identifier_headers_are_redacted_too()
    {
        // Threat T10: not credentials, but they identify the user's org.
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            logger.LogInformation(
                "anthropic-organization-id: 66460b5b-bd95-4ac4-abd8-cf12968a8d85");
        }

        ReadLog(file).Should().NotContain("66460b5b");
    }

    [Fact]
    public void Ordinary_diagnostics_survive_intact()
    {
        // A redactor that eats the useful content is worse than no logging.
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            logger.LogWarning("Usage fetch failed ({Failure}); attempt {Attempt}.", "RateLimited", 3);
        }

        ReadLog(file).Should().Contain("Usage fetch failed (RateLimited); attempt 3.");
    }

    [Fact]
    public void Each_line_carries_a_timestamp_level_and_category()
    {
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            logger.LogWarning("something happened");
        }

        string line = ReadLog(file).Trim();

        line.Should().Contain("[WRN]");
        line.Should().Contain("Subject:");
        line.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z");
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_dropped()
    {
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build(LogLevel.Warning);
        using (root)
        {
            logger.LogDebug("noisy");
            logger.LogWarning("important");
        }

        string contents = ReadLog(file);

        contents.Should().NotContain("noisy");
        contents.Should().Contain("important");
    }

    [Fact]
    public void Rolls_the_file_once_it_passes_the_size_cap_and_keeps_a_bounded_number()
    {
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            // Enough volume to cross 1 MB several times over.
            string padding = new('x', 4096);
            for (int i = 0; i < 900; i++)
            {
                logger.LogInformation("{Index} {Padding}", i, padding);
            }
        }

        string[] logs = Directory.GetFiles(_directory, "claudestatus*.log");

        logs.Length.Should().BeGreaterThan(1, "the file must actually roll");
        logs.Length.Should().BeLessThanOrEqualTo(
            RollingFileLoggerProvider.MaxRetainedFiles + 1,
            "an unbounded log would eventually fill the user's disk");
    }

    [Fact]
    public void Creates_the_config_directory_on_first_write()
    {
        Directory.Exists(_directory).Should().BeFalse();

        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        using (root)
        {
            logger.LogInformation("first line");
        }

        File.Exists(file.LogFilePath).Should().BeTrue();
    }

    [Fact]
    public void Logging_after_disposal_does_not_throw()
    {
        // Shutdown races: the monitor may log while the provider is going away.
        (ILogger logger, RollingFileLoggerProvider file, IDisposable root) = Build();
        root.Dispose();

        Action act = () => logger.LogInformation("after disposal");

        act.Should().NotThrow();
    }

    [Fact]
    public void An_unwritable_directory_does_not_take_the_app_down()
    {
        // Losing a log line is acceptable; crashing a tray app over one is not.
        var file = new RollingFileLoggerProvider(
            Path.Combine(_directory, "no\0such\0path"), LogLevel.Information);
        using var redacting = new RedactingLoggerProvider(file);

        Action act = () => redacting.CreateLogger("test").LogInformation("into the void");

        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_a_blank_config_directory()
    {
        FluentActions.Invoking(() => new RollingFileLoggerProvider("  "))
            .Should().Throw<ArgumentException>();
    }
}
