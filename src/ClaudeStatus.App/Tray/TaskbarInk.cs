using Avalonia.Controls;
using Avalonia.Media;
using ClaudeStatus.Platform;

namespace ClaudeStatus.App.Tray;

/// <summary>
/// The colours the taskbar widget uses when it blends into the taskbar instead
/// of drawing the themed card.
/// </summary>
/// <remarks>
/// <para>
/// The taskbar's own text - the clock, the date - is near-white on a dark
/// taskbar and near-black on a light one, and that is what the widget matches
/// here. These are the same inks <see cref="TrayIconRenderer"/> draws the icon
/// with, chosen against the same signal (<see cref="ITrayThemeProvider"/>), so
/// the icon and the blended widget cannot disagree about what "readable on this
/// taskbar" means.
/// </para>
/// <para>
/// Colour literals live here and nowhere in a view or a style: the views bind
/// <c>Widget.Ink</c>, <c>Widget.InkMuted</c> and <c>Widget.Track</c> as dynamic
/// resources, and <see cref="Apply"/> writes them into the window's own
/// resources on every render, so a taskbar that switches appearance while the
/// app runs is picked up on the next poll with no notification plumbing.
/// </para>
/// </remarks>
public static class TaskbarInk
{
    /// <summary>Resource key for the main text colour.</summary>
    public const string InkKey = "Widget.Ink";

    /// <summary>Resource key for labels and the unknown dash.</summary>
    public const string InkMutedKey = "Widget.InkMuted";

    /// <summary>Resource key for the bars' unfilled track.</summary>
    public const string TrackKey = "Widget.Track";

    /// <summary>Ink on a dark taskbar; the clock's white.</summary>
    private static readonly Color LightInk = Color.FromRgb(0xF2, 0xF2, 0xF2);

    /// <summary>Ink on a light taskbar; the clock's near-black.</summary>
    private static readonly Color DarkInk = Color.FromRgb(0x1A, 0x1A, 0x1A);

    /// <summary>The text colour for a taskbar appearance.</summary>
    /// <remarks>
    /// Unknown falls to the dark-taskbar ink: the widget is Windows-only and a
    /// Windows taskbar is dark unless the user has said otherwise.
    /// </remarks>
    public static Color InkFor(TrayBackground background)
        => background == TrayBackground.Light ? DarkInk : LightInk;

    /// <summary>Writes the three resources for a taskbar appearance into a resource dictionary.</summary>
    public static void Apply(IResourceDictionary resources, TrayBackground background)
    {
        ArgumentNullException.ThrowIfNull(resources);

        Color ink = InkFor(background);
        resources[InkKey] = new SolidColorBrush(ink);

        // The clock's date line is the same colour at reduced weight; a muted
        // alpha rather than a second grey keeps it right on any taskbar tint.
        resources[InkMutedKey] = new SolidColorBrush(ink, 0.7d);
        resources[TrackKey] = new SolidColorBrush(ink, 0.22d);
    }
}
