namespace ClaudeStatus.Platform;

/// <summary>One entry in a native status item's menu.</summary>
/// <param name="Title">The label, or null for a separator.</param>
/// <param name="Tag">
/// What to raise when it is picked. A plain integer because that is all a native
/// menu item can be asked to carry back - the caller encodes its own meaning into
/// it and decodes it again on the way out.
/// </param>
/// <param name="IsChecked">Whether it draws a tick.</param>
/// <param name="Submenu">Nested entries, or null for a leaf.</param>
public sealed record StatusMenuEntry(
    string? Title,
    long Tag = 0,
    bool IsChecked = false,
    IReadOnlyList<StatusMenuEntry>? Submenu = null)
{
    /// <summary>A separator line.</summary>
    public static StatusMenuEntry Separator { get; } = new((string?)null);

    /// <summary>Whether this is a separator rather than a command.</summary>
    public bool IsSeparator => Title is null;
}

/// <summary>How the status item's text should be coloured.</summary>
public enum StatusTint
{
    /// <summary>The menu bar's own colour. Almost always this one.</summary>
    Normal = 0,

    /// <summary>Past the threshold, or needing a credential.</summary>
    Alert = 1,

    /// <summary>A reading that is no longer current.</summary>
    Stale = 2,
}

/// <summary>
/// A status item the OS draws from text, rather than from a bitmap we render.
/// </summary>
/// <remarks>
/// <para>
/// Exists for the macOS menu bar, where the platform can do three things Avalonia's
/// <c>TrayIcon</c> cannot: size the item to its text instead of to a square, tell a
/// left click from a right one, and colour the text to match the menu bar through
/// dark mode, light mode and the highlight while its menu is open.
/// </para>
/// <para>
/// An interface in Core so the indicator that drives it is ordinary UI code with no
/// knowledge of Objective-C, and so it can be driven by a fake in a test that has
/// no menu bar to look at.
/// </para>
/// </remarks>
public interface INativeStatusItem : IDisposable
{
    /// <summary>Whether the item was created and is usable.</summary>
    bool IsAvailable { get; }

    /// <summary>Sets the text shown in the bar.</summary>
    /// <param name="text">The whole row, already composed and localised.</param>
    /// <param name="tint">How to colour it.</param>
    void SetTitle(string text, StatusTint tint);

    /// <summary>
    /// Sets the mark shown before the text.
    /// </summary>
    /// <remarks>
    /// PNG bytes rather than a path or a platform image type, because that is the
    /// one currency both sides already speak: the caller can draw whatever it likes
    /// and this interface stays free of any drawing framework.
    /// </remarks>
    /// <param name="png">The image, or an empty span to show no mark at all.</param>
    void SetIcon(ReadOnlySpan<byte> png);

    /// <summary>Replaces the menu shown on a secondary click.</summary>
    void SetMenu(IReadOnlyList<StatusMenuEntry> entries);

    /// <summary>Shows or hides the item.</summary>
    void SetVisible(bool visible);

    /// <summary>Raised on a primary click - the gesture that means "show me".</summary>
    event EventHandler? LeftClicked;

    /// <summary>Raised with the <see cref="StatusMenuEntry.Tag"/> the user picked.</summary>
    event EventHandler<long>? MenuItemClicked;
}
