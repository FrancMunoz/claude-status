namespace ClaudeStatus.Platform;

/// <summary>
/// Starts the app when the user logs in.
/// </summary>
/// <remarks>
/// Per-user, never machine-wide: this is a tray app for one person's account,
/// and a machine-wide entry would need elevation we should never ask for.
/// </remarks>
public interface IAutostart
{
    /// <summary>Resource key naming the mechanism, e.g. <c>"Autostart_WindowsRegistry"</c>.</summary>
    string DescriptionKey { get; }

    /// <summary>
    /// True when the mechanism can be used at all.
    /// </summary>
    /// <remarks>
    /// False in a portable or sandboxed layout where we cannot determine our own
    /// executable path. The Config window disables the checkbox rather than
    /// offering a switch that silently does nothing.
    /// </remarks>
    bool IsSupported { get; }

    /// <summary>Whether the app is currently registered to start at login.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>Registers or unregisters. Setting the current value again is a no-op.</summary>
    /// <exception cref="AutostartException">The OS refused.</exception>
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
}

/// <summary>Autostart could not be changed. Never carries secret material.</summary>
/// <remarks>
/// Carries a <see cref="MessageKey"/> alongside the usual English
/// <see cref="Exception.Message"/>. The message is what lands in the log, where
/// English is what a bug report wants; the key is what the Config window shows,
/// where the user's own language is what they want. Keeping both means neither
/// audience is served badly.
/// </remarks>
public sealed class AutostartException : Exception
{
    /// <summary>The key used when a throw site does not name a more specific one.</summary>
    public const string DefaultMessageKey = "Autostart_Error_Generic";

    public AutostartException()
        : base("The autostart setting could not be changed.")
    {
    }

    public AutostartException(string message)
        : base(message)
    {
    }

    public AutostartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="messageKey">Resource key for the user-facing explanation.</param>
    /// <param name="message">English text for the log.</param>
    public AutostartException(string messageKey, string message, Exception? innerException = null)
        : base(message, innerException)
        => MessageKey = messageKey;

    /// <summary>Resource key for the explanation shown in Config.</summary>
    public string MessageKey { get; init; } = DefaultMessageKey;
}
