using System.Text.Json.Serialization;
using ClaudeStatus.Localization;
using ClaudeStatus.Theming;
using ClaudeStatus.Usage;

namespace ClaudeStatus.Config;

/// <summary>Where the access token comes from.</summary>
public enum CredentialSource
{
    /// <summary>Read Claude Code's existing login at poll time. Stores nothing. The default.</summary>
    ClaudeCodeLogin = 0,

    /// <summary>A token the user pasted, held in the OS secret store. Advanced fallback only.</summary>
    ManualToken = 1,
}

/// <summary>How usage is shown on the desktop.</summary>
public enum IndicatorKind
{
    /// <summary>
    /// A card inside the taskbar with all three metrics. Windows only; anywhere it
    /// cannot be used the app quietly falls back to <see cref="TrayIcon"/>.
    /// </summary>
    TaskbarWidget = 0,

    /// <summary>One metric as a number in the notification area.</summary>
    TrayIcon = 1,
}

/// <summary>
/// Timing policy for <see cref="UsageMonitor"/>.
/// </summary>
/// <remarks>
/// The floors here are not preferences. The endpoint 429s aggressively and can
/// stay throttled for a whole session, so <see cref="BaseInterval"/> is clamped
/// to 60 s no matter what lands in the config file.
/// </remarks>
public sealed record PollingOptions
{
    /// <summary>Hard floor on the poll interval. Not user-overridable.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);

    /// <summary>Interval while everything is succeeding.</summary>
    public TimeSpan BaseInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Ceiling that exponential backoff climbs to.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>Cap applied to a server-supplied <c>Retry-After</c>.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Minimum gap between manual Refresh presses.</summary>
    public TimeSpan ForcedRefreshCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How much of a computed delay jitter may shave off, 0-1.</summary>
    public double JitterFraction { get; init; } = 0.2d;

    /// <summary>Forces every value into a sane range. Always call this on anything loaded from disk.</summary>
    public PollingOptions Normalized()
    {
        TimeSpan @base = BaseInterval < MinimumInterval ? MinimumInterval : BaseInterval;
        TimeSpan max = MaxInterval < @base ? @base : MaxInterval;
        TimeSpan retryCap = MaxRetryAfter < max ? max : MaxRetryAfter;
        TimeSpan cooldown = ForcedRefreshCooldown < TimeSpan.Zero ? TimeSpan.Zero : ForcedRefreshCooldown;
        double jitter = double.IsFinite(JitterFraction) ? Math.Clamp(JitterFraction, 0d, 1d) : 0d;

        return this with
        {
            BaseInterval = @base,
            MaxInterval = max,
            MaxRetryAfter = retryCap,
            ForcedRefreshCooldown = cooldown,
            JitterFraction = jitter,
        };
    }
}

/// <summary>
/// Everything the app persists.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type never holds secret bytes.</b> <see cref="HasCredential"/> is a
/// flag, not a credential. The token itself lives only in the OS secret store,
/// or nowhere at all under the default <see cref="CredentialSource.ClaudeCodeLogin"/>.
/// See <c>docs/security.md</c> §4.
/// </para>
/// <para>
/// <b>A property initializer here is not a default that survives a round trip.</b>
/// The JSON source generator constructs this object without running them, so a
/// key absent from <c>settings.json</c> comes back as <c>default</c>, not as the
/// value written next to the property. Reflection-based <c>JsonSerializer</c>
/// does not behave that way, which is what kept it hidden: measured 2026-09-05,
/// the same document gives <c>SchemaVersion</c> 1 through reflection and 0
/// through the generated context.
/// </para>
/// <para>
/// Most properties never showed it because <see cref="Normalized"/> repairs them
/// - an empty <see cref="ThemeId"/> or a null <see cref="Polling"/> is restored a
/// step later, so the default appears to work. It bit the first <c>bool</c> whose
/// intended default was <c>true</c>, where <c>false</c> is a legitimate value and
/// nothing can tell the two apart afterwards.
/// </para>
/// <para>
/// So: <b>every persisted property must have <c>default</c> as its intended
/// value</b>, or be repaired in <see cref="Normalized"/>. That is why the update
/// setting is stored as <see cref="DisableAutomaticUpdates"/> rather than as the
/// positive flag the user interface shows.
/// </para>
/// <para>
/// When a wanted default is not <c>default</c> and <c>default</c> is itself a
/// legitimate choice, the property is made nullable so the two can be told apart,
/// and <see cref="Normalized"/> resolves the null. <see cref="OsdTransparency"/>
/// is the worked example: 0 means "solid", which a user may genuinely want, so it
/// cannot double as "not set".
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Bumped when a migration is needed. Unknown future versions load on a best-effort basis.</summary>
    /// <remarks>
    /// Repaired in <see cref="Normalized"/> rather than trusted from disk: an
    /// absent key deserializes as 0, which is not a version this app ever wrote.
    /// See the warning on this type.
    /// </remarks>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Which metric the tray icon shows.
    /// </summary>
    /// <remarks>
    /// <see cref="IndicatorMode.Row"/> by default, which is the richest reading a
    /// tray can give and costs nothing where it cannot be drawn: a tray that needs
    /// a square icon renders it as <see cref="IndicatorMode.SessionPercent"/>, the
    /// previous default, so this changes nothing on Windows or Linux.
    /// </remarks>
    public IndicatorMode IndicatorMode { get; init; } = IndicatorMode.Row;

    /// <summary>Widget or icon. The widget is the default; the icon is the fallback.</summary>
    public IndicatorKind Indicator { get; init; } = IndicatorKind.TaskbarWidget;

    /// <summary>
    /// Whether the taskbar widget shows the weekly Fable limit as a third column.
    /// </summary>
    /// <remarks>
    /// Off by default: most plans hit the session or weekly limit long before the
    /// Fable one, and the widget is a third narrower without it. The popup and
    /// the report always show all three.
    /// </remarks>
    public bool ShowFableInWidget { get; init; }

    /// <summary>
    /// Whether the taskbar widget draws the theme's card instead of blending into
    /// the taskbar.
    /// </summary>
    /// <remarks>
    /// Stored inverted, like <see cref="DisableAutomaticUpdates"/>, because the
    /// intended default is "blend" and a persisted <c>bool</c> can only default to
    /// <c>false</c> - see the warning on this type. Read it through
    /// <see cref="WidgetFollowsSystem"/>.
    /// </remarks>
    public bool WidgetUsesThemedCard { get; init; }

    /// <summary>
    /// Whether the taskbar widget blends into the taskbar: transparent background,
    /// no border, and the same text colour the clock uses.
    /// </summary>
    /// <remarks>
    /// On by default (decided 2026-09-06): a taskbar widget should look like part
    /// of the taskbar. Blended, the text sits on the taskbar itself, so its colour
    /// follows the taskbar's light or dark appearance exactly as the tray icon's
    /// does. Off, the widget draws the theme's card instead.
    /// </remarks>
    [JsonIgnore]
    public bool WidgetFollowsSystem => !WidgetUsesThemedCard;

    /// <summary>
    /// Whether the app stays quiet when usage climbs fast enough to run out before
    /// the window resets. Stored inverted so the intended default - warn - is
    /// <c>default</c>; see the warning on this type and <see cref="VelocityAlerts"/>.
    /// </summary>
    public bool DisableVelocityAlerts { get; init; }

    /// <summary>Warn when the current pace would exhaust a window before it resets.</summary>
    [JsonIgnore]
    public bool VelocityAlerts => !DisableVelocityAlerts;

    /// <summary>Percentage at or above which the icon turns red.</summary>
    public double ThresholdPercent { get; init; } = ThresholdEvaluator.DefaultThresholdPercent;

    /// <summary>Where the token comes from.</summary>
    public CredentialSource CredentialSource { get; init; } = CredentialSource.ClaudeCodeLogin;

    /// <summary>
    /// Whether a manual token has been stored. A flag only - never the token.
    /// Meaningless when <see cref="CredentialSource"/> is
    /// <see cref="CredentialSource.ClaudeCodeLogin"/>.
    /// </summary>
    public bool HasCredential { get; init; }

    /// <summary>Start with the OS session.</summary>
    public bool StartWithOperatingSystem { get; init; }

    /// <summary>Use <see cref="FakeUsageProvider"/> instead of the real endpoint.</summary>
    public bool UseFakeProvider { get; init; }

    /// <summary>
    /// Look for new versions on GitHub, and download them in the background.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, because an install that never updates itself is an install
    /// that stays on whatever version it was first given - and a bug fixed here
    /// reaches nobody. A downloaded update is applied at the next start and never
    /// mid-session, so being on costs the user no interruption.
    /// </para>
    /// <para>
    /// Turning it off stops the network call entirely; it does not merely hide the
    /// notice. This is the only request the app makes to anything other than the
    /// Anthropic endpoint, so it has to be genuinely switchable.
    /// </para>
    /// <para>
    /// <b>Stored inverted</b>, so that the default - an absent key, which is what
    /// every settings file written before this feature existed has - means updates
    /// are on. See the warning on this type: a property initializer would not have
    /// survived the round trip, and this flag is the one place where a wrong
    /// default has no visible symptom at all.
    /// </para>
    /// </remarks>
    public bool DisableAutomaticUpdates { get; init; }

    /// <summary>Whether to check for new versions. The positive form, for callers.</summary>
    [JsonIgnore]
    public bool AutomaticUpdates => !DisableAutomaticUpdates;

    /// <summary>Timing policy.</summary>
    public PollingOptions Polling { get; init; } = new();

    /// <summary>
    /// Interface language as a BCP-47 tag, or empty to follow the operating system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The only free-text field this type has</b>, and a deliberate exception to
    /// the rule that settings hold no strings. That rule exists so a credential can
    /// never end up in <c>settings.json</c>, and the exception is safe because
    /// <see cref="Normalized"/> discards anything that is not a short language tag:
    /// at most <see cref="LanguageCatalog.MaximumTagLength"/> characters, letters
    /// and one optional hyphenated subtag. No token fits through that.
    /// </para>
    /// <para>
    /// Enumerating the shipped languages instead would have been airtight, but it
    /// would also make it impossible to select a language added as a loose JSON
    /// file, which is the whole point of that mechanism. See <c>docs/security.md</c> §4.
    /// </para>
    /// </remarks>
    public string LanguageTag { get; init; } = LanguageCatalog.FollowSystem;

    /// <summary>
    /// The colour theme, or <see cref="ThemeCatalog.SystemId"/> to follow the OS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ThemeCatalog.DefaultId"/> by default, not <see cref="ThemeCatalog.SystemId"/>.
    /// Following the OS is a choice the user can make and is recorded as one.
    /// </para>
    /// <para>
    /// Bounded the same way as <see cref="LanguageTag"/>: short, lower-case ASCII,
    /// no separators. It also becomes a file name when the theme comes from
    /// <c>themes/</c>, so the validation is what stops a path escaping that folder.
    /// </para>
    /// </remarks>
    public string ThemeId { get; init; } = ThemeCatalog.DefaultId;

    /// <summary>
    /// The interface font family, or empty for the platform default.
    /// </summary>
    /// <remarks>
    /// The loosest field in this type, and therefore the one with the most
    /// validation: capped at 64 characters, restricted to the characters a real
    /// font stack uses, and additionally rejected if it looks credential-shaped.
    /// See <c>docs/security.md</c> §4.1.
    /// </remarks>
    public string FontFamily { get; init; } = FontCatalog.SystemDefault;

    /// <summary>The transparency the popup gets when the user has not chosen one.</summary>
    /// <remarks>
    /// Enough to show that the popup is an overlay and not a window, and little
    /// enough that nothing behind it competes with the readings. Every built-in
    /// theme still clears its contrast checks here: only the background carries
    /// the alpha, so the text is unaffected at any setting.
    /// </remarks>
    public const double DefaultOsdTransparency = 0.10d;

    /// <summary>
    /// Background transparency of the details popup, 0 (solid) to 1 (invisible),
    /// or <c>null</c> for <see cref="DefaultOsdTransparency"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The popup only. It is the one window that behaves like an overlay, and it
    /// is the only one where seeing what is underneath is useful rather than
    /// distracting. Text stays fully opaque at every setting - a translucent
    /// number is a number you have to squint at.
    /// </para>
    /// <para>
    /// <b>Transparency, not opacity.</b> The two are the same slider read from
    /// opposite ends, and the whole app uses this direction so the word never has
    /// to be mentally inverted: 0 means "add no transparency". Expressed as
    /// opacity, "leave it alone" would be the number 1, which reads like a
    /// setting already turned on.
    /// </para>
    /// <para>
    /// <b>Nullable because the default is no longer <c>default</c>.</b> It was 0
    /// until 2026-09-07, which let this be a plain <c>double</c> - see the warning
    /// on this type. Now that the default is <see cref="DefaultOsdTransparency"/>,
    /// a plain <c>double</c> could not tell "the user asked for a solid panel"
    /// from "this key is not in the file": the first must stay solid, the second
    /// must become the default, and both arrive as 0. <c>null</c> is the absent
    /// case, and <see cref="Normalized"/> resolves it, exactly as it already does
    /// for <see cref="Polling"/>.
    /// </para>
    /// <para>
    /// A full 1 is a real setting, not a degenerate one: the panel and its border
    /// disappear and the readings float over the desktop, which is what an
    /// on-screen display traditionally looks like. Because only the background
    /// carries the alpha, the text stays legible and the buttons stay clickable.
    /// </para>
    /// </remarks>
    public double? OsdTransparency { get; init; }

    /// <summary>Clamps anything that arrived from disk into a usable range.</summary>
    public AppSettings Normalized() => this with
    {
        // 0 is what an absent key deserializes to, and no version this app ever
        // wrote. Anything else is left alone so a future migration can read it.
        SchemaVersion = SchemaVersion <= 0 ? 1 : SchemaVersion,
        ThresholdPercent = ThresholdEvaluator.Clamp(ThresholdPercent),
        IndicatorMode = Enum.IsDefined(IndicatorMode) ? IndicatorMode : IndicatorMode.SessionPercent,
        Indicator = Enum.IsDefined(Indicator) ? Indicator : IndicatorKind.TaskbarWidget,
        CredentialSource = Enum.IsDefined(CredentialSource) ? CredentialSource : CredentialSource.ClaudeCodeLogin,
        Polling = (Polling ?? new PollingOptions()).Normalized(),
        LanguageTag = LanguageCatalog.IsValidTag(LanguageTag) ? LanguageTag : LanguageCatalog.FollowSystem,
        // An absent key arrives here as null (the source generator skips the
        // initialiser above), so this line is what actually sets the default for
        // an existing settings file. Both must name the same theme.
        ThemeId = ThemeCatalog.IsValidId(ThemeId) ? ThemeId : ThemeCatalog.DefaultId,
        FontFamily = FontCatalog.Normalize(FontFamily),
        // Absent (null) and unusable (NaN, infinity) both become the default; a
        // real number is clamped. After this the value is never null, so nothing
        // downstream has to know that "not chosen" was ever representable.
        OsdTransparency = OsdTransparency is double t && double.IsFinite(t)
            ? Math.Clamp(t, 0d, 1d)
            : DefaultOsdTransparency,
    };
}
