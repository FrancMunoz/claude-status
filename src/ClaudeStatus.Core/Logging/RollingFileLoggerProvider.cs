using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClaudeStatus.Logging;

/// <summary>
/// Writes log lines to a size-rotated file in the config directory.
/// </summary>
/// <remarks>
/// <para>
/// Hand-rolled rather than taking a Serilog or NLog dependency. What this app
/// needs is "append a line, roll at a megabyte, keep a few" - a few dozen lines
/// of code - and <c>docs/manual.md</c> §8 asks for a real justification before adding
/// a package. A logging framework sitting next to the credential path is also
/// more surface than this earns.
/// </para>
/// <para>
/// <b>Nothing here may throw.</b> A tray app that dies because its log file is
/// locked or the disk is full would be a far worse bug than the missing log line.
/// Every failure path silently gives up.
/// </para>
/// <para>
/// This provider does <b>not</b> redact. Wrap it in
/// <see cref="RedactingLoggerProvider"/> - which is what
/// <c>AddClaudeStatusLogging</c> does - so nothing reaches the file unscrubbed.
/// </para>
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    /// <summary>The current log file's name.</summary>
    public const string FileName = "claudestatus.log";

    /// <summary>Roll once the file passes this size.</summary>
    public const long MaxFileSizeBytes = 1024 * 1024;

    /// <summary>How many rolled files to keep, besides the current one.</summary>
    public const int MaxRetainedFiles = 3;

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private bool _disposed;

    public RollingFileLoggerProvider(string configDirectory, LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = configDirectory;
        _minimumLevel = minimumLevel;
        LogFilePath = Path.Combine(_directory, FileName);
    }

    /// <summary>The file currently being written, for display in Config and Info.</summary>
    public string LogFilePath { get; }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() => _disposed = true;

    /// <summary>Appends one already-redacted line.</summary>
    private void Write(string line)
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                RollIfNeeded();
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                // Disk full, file locked, path gone. Losing a log line is acceptable;
                // taking the app down over it is not.
            }
        }
    }

    /// <summary>
    /// Shuffles the current file aside once it grows past the cap.
    /// </summary>
    /// <remarks>
    /// <c>claudestatus.log</c> becomes <c>.1.log</c>, <c>.1</c> becomes <c>.2</c>,
    /// and the oldest is deleted. Called under the write lock.
    /// </remarks>
    private void RollIfNeeded()
    {
        var current = new FileInfo(LogFilePath);
        if (!current.Exists || current.Length < MaxFileSizeBytes)
        {
            return;
        }

        string Rolled(int index) => Path.Combine(_directory, $"claudestatus.{index}.log");

        string oldest = Rolled(MaxRetainedFiles);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int index = MaxRetainedFiles - 1; index >= 1; index--)
        {
            string source = Rolled(index);
            if (File.Exists(source))
            {
                File.Move(source, Rolled(index + 1), overwrite: true);
            }
        }

        File.Move(LogFilePath, Rolled(1), overwrite: true);
    }

    /// <summary>Formats one entry. Scopes are ignored - never put a secret in one.</summary>
    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && logLevel >= owner._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            string timestamp = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd HH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            // The short category keeps lines readable; the full one is noise.
            int lastDot = category.LastIndexOf('.');
            string shortCategory = lastDot >= 0 ? category[(lastDot + 1)..] : category;

            var builder = new StringBuilder()
                .Append(timestamp)
                .Append(" [").Append(Describe(logLevel)).Append("] ")
                .Append(shortCategory).Append(": ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                builder.Append(Environment.NewLine).Append(exception);
            }

            owner.Write(builder.ToString());
        }

        private static string Describe(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
