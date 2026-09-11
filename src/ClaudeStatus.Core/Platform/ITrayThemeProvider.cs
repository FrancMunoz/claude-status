namespace ClaudeStatus.Platform;

/// <summary>What the tray or menu bar is painted with, behind our icon.</summary>
public enum TrayBackground
{
    /// <summary>
    /// Could not be determined.
    /// </summary>
    /// <remarks>
    /// The normal answer on Linux, where there is no portable signal for the
    /// panel's colour. The renderer draws a contrasting outline in this case so
    /// the glyph reads either way.
    /// </remarks>
    Unknown = 0,

    /// <summary>A dark tray. Light ink.</summary>
    Dark = 1,

    /// <summary>A light tray. Dark ink.</summary>
    Light = 2,
}

/// <summary>
/// Reports the tray background so the icon can be drawn to contrast with it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a real bug: the icon was drawn in near-white and became
/// invisible on the Windows light theme, which is the default on a fresh install.
/// It was legible only in its red alert state — readable exactly when something
/// was wrong and invisible the rest of the time.
/// </para>
/// <para>
/// macOS needs none of this: a template icon is recoloured for the menu bar
/// automatically. This is a Windows and Linux problem.
/// </para>
/// </remarks>
public interface ITrayThemeProvider
{
    /// <summary>
    /// The current tray background.
    /// </summary>
    /// <remarks>
    /// Read on every render rather than cached, so a render always draws against
    /// the background as it is now. Implementations must therefore be cheap and
    /// must never throw.
    /// </remarks>
    TrayBackground Current { get; }

    /// <summary>
    /// Raised when <see cref="Current"/> has changed.
    /// </summary>
    /// <remarks>
    /// Without it a light/dark switch waited for the next poll - up to a minute of
    /// ink drawn for the old background, which on the new one is barely there.
    /// Raised on whatever thread noticed the change, so a subscriber that touches
    /// UI must marshal. A provider with no signal never raises it.
    /// </remarks>
    event EventHandler? Changed;
}

/// <summary>
/// A provider that always reports the same thing.
/// </summary>
/// <remarks>
/// Used on platforms with no usable signal — macOS, where the template icon
/// handles it, and Linux, where the panel could be anything.
/// </remarks>
public sealed class StaticTrayThemeProvider(TrayBackground background) : ITrayThemeProvider
{
    /// <inheritdoc />
    public TrayBackground Current { get; } = background;

    /// <inheritdoc />
    /// <remarks>Never raised: nothing here can change.</remarks>
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
