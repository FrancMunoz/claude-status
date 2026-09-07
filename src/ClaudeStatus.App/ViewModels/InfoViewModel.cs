using System.IO;
using System.Reflection;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Usage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// The Info window: what this is, what version, and where the numbers come from.
/// </summary>
/// <remarks>
/// The unofficial-data-source notice is required by <c>docs/manual.md</c> §3 and
/// <c>docs/data-source.md</c>. Someone comparing our numbers against claude.ai
/// deserves to know they are reading an undocumented endpoint that can change
/// without warning.
/// </remarks>
public sealed class InfoViewModel : ObservableObject
{
    private readonly IPlatformInfo _platform;
    private readonly IUsageProvider _provider;
    private readonly ILocalizer _l;

    public InfoViewModel(IPlatformInfo platform, IUsageProvider provider, ILocalizer localizer)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));

        // Every line here is composed, so a language change has to invalidate the
        // lot; naming no property is how INotifyPropertyChanged says "all of them".
        _l.PropertyChanged += (_, _) => OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(null));
    }

    /// <summary>The localizer, for static labels bound as <c>L[Key]</c>.</summary>
    public ILocalizer L => _l;

    /// <summary>The product name. A brand, so never translated.</summary>
    public static string ApplicationName => "ClaudeStatus";

    /// <summary>
    /// The version, from the assembly.
    /// </summary>
    /// <remarks>
    /// MinVer stamps this from the git tag at build time. Never hand-edited
    /// (<c>docs/manual.md</c> §8).
    /// </remarks>
    public static string Version =>
        typeof(InfoViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(InfoViewModel).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>One-line description.</summary>
    public string Tagline => _l["Info_Tagline"];

    /// <summary>The version line.</summary>
    public string VersionText => _l.Format("Info_Version", Version);

    /// <summary>
    /// The OS we are running on.
    /// </summary>
    /// <remarks>
    /// The OS name itself comes from <see cref="System.Runtime.InteropServices.RuntimeInformation"/>
    /// and is whatever the platform reports, so only the sentence around it is
    /// translated. Rewriting "Microsoft Windows 10.0.26200" would help nobody.
    /// </remarks>
    public string OperatingSystemText => _l.Format("Info_RunningOn", _platform.OperatingSystemName);

    /// <summary>Where settings and logs live.</summary>
    public string ConfigDirectoryText => _l.Format("Info_ConfigFolder", _platform.ConfigDirectory);

    /// <summary>Which provider is supplying the numbers.</summary>
    public string DataSourceText => _l.Format(
        "Info_DataSource", _l.Format(_provider.NameKey, _provider.NameArgument));

    /// <summary>
    /// The log file, so someone filing a bug can find it.
    /// </summary>
    /// <remarks>
    /// Safe to show: every line goes through <c>RedactingLoggerProvider</c>, so
    /// the file holds no credential material (<c>docs/security.md</c> threat T1).
    /// </remarks>
    public string LogFileText => _l.Format(
        "Info_LogFile",
        Path.Combine(_platform.ConfigDirectory, Logging.RollingFileLoggerProvider.FileName));

    /// <summary>The disclaimer. Required, and deliberately blunt.</summary>
    public string UnofficialNotice => _l["Info_UnofficialNotice"];

    /// <summary>Licence line.</summary>
    public string License => _l["Info_License"];

    /// <summary>Project home. A URL, so never translated.</summary>
    public static string ProjectUrl => "https://github.com/FrancMunoz/claude-status";

    /// <summary>
    /// The heart in "Made with ❤ in Menorca".
    /// </summary>
    /// <remarks>
    /// U+2764 without the emoji variation selector, so the platform picks the
    /// text glyph and the foreground brush decides its colour. With the selector
    /// it becomes a colour emoji whose red is the font's, not the theme's, and
    /// the one line on this window that is meant to be warm ends up clashing
    /// with whichever palette is in use.
    /// </remarks>
    public static string Heart => "❤";

    /// <summary>The text before the heart in the "made in" line.</summary>
    public string MadeInBefore => MadeInPart(0);

    /// <summary>The text after the heart in the "made in" line.</summary>
    public string MadeInAfter => MadeInPart(1);

    /// <summary>
    /// Splits the "made in" template around its placeholder.
    /// </summary>
    /// <remarks>
    /// The heart has to be its own control to be coloured on its own, but the
    /// words around it move: German puts the verb at the end. So the template
    /// keeps the word order and the placeholder marks where the symbol goes,
    /// exactly as <see cref="ILocalizer.Format"/> would - only here the argument
    /// is a control rather than a string.
    /// </remarks>
    private string MadeInPart(int part)
    {
        string template = _l["Info_MadeIn"];
        int at = template.IndexOf("{0}", StringComparison.Ordinal);

        if (at < 0)
        {
            // A translation that lost its placeholder still has to read as a
            // sentence; showing it whole beats showing half of it.
            return part == 0 ? template : string.Empty;
        }

        return part == 0 ? template[..at] : template[(at + 3)..];
    }
}
