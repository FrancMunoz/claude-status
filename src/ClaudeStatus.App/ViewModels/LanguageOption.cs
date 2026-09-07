namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// One entry in the Config window's language picker.
/// </summary>
/// <param name="Tag">
/// The BCP-47 tag stored in settings, or empty for "follow the operating system".
/// </param>
/// <param name="DisplayName">
/// What the list shows: the language's own name for a real language, or the
/// translated "System default" for the first entry.
/// </param>
/// <remarks>
/// A record rather than a bare string so the empty "follow the system" tag can
/// carry a readable label without the view having to special-case it.
/// </remarks>
public sealed record LanguageOption(string Tag, string DisplayName)
{
    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
