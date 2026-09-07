using System.Globalization;
using System.Linq;
using Avalonia;
using ClaudeStatus.App.Theming;
using ClaudeStatus.Config;
using ClaudeStatus.Localization;
using ClaudeStatus.Platform;
using ClaudeStatus.Security;
using ClaudeStatus.Theming;
using ClaudeStatus.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// The configuration window.
/// </summary>
/// <remarks>
/// <para>
/// Opens automatically on first run. The credential field is the only place in
/// the app where a secret exists as a <see cref="string"/>, and it is converted
/// to bytes and cleared the moment it is submitted (<c>docs/manual.md</c> §8).
/// </para>
/// <para>
/// The warning banner is not decoration. When the store reports
/// <see cref="ISecretStore.IsHardened"/> as false - the Linux no-keyring case -
/// the user is told, because their token is then only as safe as the file
/// permissions (<c>docs/security.md</c> threat T11).
/// </para>
/// </remarks>
public partial class ConfigViewModel : ObservableObject
{
    private readonly CredentialService _credentials;
    private readonly IAutostart _autostart;
    private readonly IPlatformInfo _platform;
    private readonly ITaskbarHost _taskbarHost;
    private readonly IConfigStore _configStore;
    private readonly Localizer _localizer;
    private readonly JsonLanguageStore _languageStore;
    private readonly JsonThemeStore _themeStore;
    private readonly Func<bool> _systemIsDark;
    private readonly Func<AppSettings> _current;
    private readonly Func<AppSettings, Task> _apply;

    /// <summary>Set while <see cref="LoadAsync"/> writes the form, to suppress side effects.</summary>
    private bool _loading;

    [ObservableProperty]
    private bool _useClaudeCodeLogin = true;

    /// <summary>Widget rather than icon. Only offered where a taskbar can host it.</summary>
    [ObservableProperty]
    private bool _useTaskbarWidget = true;

    /// <summary>Whether the widget gets a third column for the Fable limit.</summary>
    [ObservableProperty]
    private bool _showFableInWidget;

    /// <summary>Whether the widget blends into the taskbar instead of drawing the themed card.</summary>
    [ObservableProperty]
    private bool _widgetFollowsSystem = true;

    [ObservableProperty]
    private string _tokenInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThresholdText))]
    private double _thresholdPercent = ThresholdEvaluator.DefaultThresholdPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntervalText))]
    private double _pollIntervalSeconds = 60;

    [ObservableProperty]
    private bool _startWithOperatingSystem;

    [ObservableProperty]
    private bool _useFakeProvider;

    /// <summary>Look for new versions on GitHub. See <see cref="AppSettings.AutomaticUpdates"/>.</summary>
    [ObservableProperty]
    private bool _automaticUpdates = true;

    /// <summary>Warn when usage climbs fast enough to run out before a window resets.</summary>
    [ObservableProperty]
    private bool _velocityAlerts = true;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasStoredCredential;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private FontOption _selectedFont;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransparencyText))]
    private double _osdTransparencyPercent;

    /// <summary>The typed family, used when <see cref="SelectedFont"/> is the custom entry.</summary>
    [ObservableProperty]
    private string _customFont = string.Empty;

    public ConfigViewModel(
        CredentialService credentials,
        IAutostart autostart,
        IPlatformInfo platform,
        ITaskbarHost taskbarHost,
        IConfigStore configStore,
        Localizer localizer,
        JsonLanguageStore languageStore,
        JsonThemeStore themeStore,
        Func<bool> systemIsDark,
        Func<AppSettings> current,
        Func<AppSettings, Task> apply)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _taskbarHost = taskbarHost ?? throw new ArgumentNullException(nameof(taskbarHost));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _languageStore = languageStore ?? throw new ArgumentNullException(nameof(languageStore));
        _themeStore = themeStore ?? throw new ArgumentNullException(nameof(themeStore));
        _systemIsDark = systemIsDark ?? throw new ArgumentNullException(nameof(systemIsDark));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));

        Languages = BuildLanguageList();
        _selectedLanguage = Languages[0];

        Themes = BuildThemeList();
        _selectedTheme = Themes[0];

        Fonts = BuildFontList();
        _selectedFont = Fonts[0];

        _localizer.PropertyChanged += (_, _) => OnLanguageChanged();
    }

    /// <summary>The localizer, for static labels bound as <c>L[Key]</c>.</summary>
    public ILocalizer L => _localizer;

    /// <summary>The lowest poll interval the UI will let the user choose.</summary>
    public static double MinimumPollSeconds => PollingOptions.MinimumInterval.TotalSeconds;

    /// <summary>Every language on offer, "system default" first.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; private set; }

    /// <summary>Which mechanism protects a stored token, for display.</summary>
    public string StoreDescription => _localizer[_credentials.StoreDescriptionKey];

    /// <summary>Where autostart is registered, for display.</summary>
    public string AutostartDescription => _localizer[_autostart.DescriptionKey];

    /// <summary>Every theme on offer, "follow the system" first.</summary>
    public IReadOnlyList<ThemeOption> Themes { get; private set; }

    /// <summary>The suggested fonts, plus the platform default and a custom entry.</summary>
    public IReadOnlyList<FontOption> Fonts { get; private set; }

    /// <summary>Where to put a translation file, shown under the language picker.</summary>
    public string LanguageHelp => _localizer.Format("Config_Language_Help", _languageStore.Directory);

    /// <summary>Where to put a theme file, shown under the theme picker.</summary>
    public string ThemeHelp => _localizer.Format("Config_Theme_Help", _themeStore.Directory);

    /// <summary>The opacity slider's caption.</summary>
    public string TransparencyText => _localizer.Format("Config_Transparency", OsdTransparencyPercent);

    /// <summary>Whether the custom-font text box is usable.</summary>
    public bool IsCustomFont => SelectedFont?.IsCustom ?? false;

    /// <summary>The slider's caption, with the live value folded in.</summary>
    /// <remarks>
    /// Composed here rather than with a <c>StringFormat</c> in the view, because
    /// the format string itself is translated and cannot live in the AXAML.
    /// </remarks>
    public string ThresholdText => _localizer.Format("Config_Threshold", ThresholdPercent);

    /// <summary>The poll-interval slider's caption.</summary>
    public string IntervalText => _localizer.Format("Config_Interval", PollIntervalSeconds);

    /// <summary>Where the settings file lives, shown so the user can find or delete it.</summary>
    public string SettingsFileText => _localizer.Format("Config_SettingsFile", ConfigFilePath);

    /// <summary>False when we could not work out our own executable path.</summary>
    public bool IsAutostartSupported => _autostart.IsSupported;

    /// <summary>Where settings live, shown so the user can find or delete them.</summary>
    public string ConfigFilePath => _configStore.SettingsFilePath;

    /// <summary>
    /// The standing warning that a stored credential cannot be copied elsewhere.
    /// </summary>
    /// <remarks>
    /// Required by <c>docs/security.md</c> §4. Config is deliberately not portable, and
    /// a user who copies their profile to a new machine should learn that here
    /// rather than from a mysterious failure.
    /// </remarks>
    public string EncryptionNotice => _localizer.Format("Config_EncryptionNotice", StoreDescription);

    /// <summary>True when the secret store is weaker than a real keyring.</summary>
    public bool ShowWeakStoreWarning => !_credentials.IsStoreHardened;

    /// <summary>The warning text shown when <see cref="ShowWeakStoreWarning"/> is true.</summary>
    public string WeakStoreWarning => _localizer["Config_WeakStoreWarning"];

    /// <summary>
    /// Whether the indicator choice is offered at all.
    /// </summary>
    /// <remarks>
    /// Only where a horizontal taskbar exists to host the widget - Windows, in
    /// practice. Elsewhere the icon is the only option, and a radio group with one
    /// live entry would be a question with no answer.
    /// </remarks>
    public bool CanUseTaskbarWidget => _taskbarHost.IsSupported;

    /// <summary>
    /// Whether the indicator draws every limit in a row, and so whether the Fable
    /// column is a choice worth offering.
    /// </summary>
    /// <remarks>
    /// The same setting the taskbar widget uses, reached through a second control
    /// because the widget's own block is hidden where no taskbar can host it - and
    /// that is exactly where the menu bar row lives. Without this the setting was
    /// unreachable on the one platform that can draw the third column.
    /// </remarks>
    public bool CanUseInlineRow => _platform.SupportsInlineTrayText;

    /// <summary>True when the tray may not work on this desktop.</summary>
    public bool ShowTrayWarning => _platform.TraySupport != TraySupport.Available;

    /// <summary>The warning text shown when <see cref="ShowTrayWarning"/> is true.</summary>
    public string TrayWarning => _localizer[_platform.TraySupport == TraySupport.Unavailable
        ? "Config_TrayWarning_NoSession"
        : "Config_TrayWarning_Unlikely"];

    /// <summary>Loads the current settings into the form.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        AppSettings settings = _current();

        _loading = true;
        try
        {
            UseClaudeCodeLogin = settings.CredentialSource == CredentialSource.ClaudeCodeLogin;
            UseTaskbarWidget = settings.Indicator == IndicatorKind.TaskbarWidget;
            ShowFableInWidget = settings.ShowFableInWidget;
            WidgetFollowsSystem = settings.WidgetFollowsSystem;
            ThresholdPercent = settings.ThresholdPercent;
            PollIntervalSeconds = settings.Polling.BaseInterval.TotalSeconds;
            UseFakeProvider = settings.UseFakeProvider;
            AutomaticUpdates = settings.AutomaticUpdates;
            VelocityAlerts = settings.VelocityAlerts;

            // Rebuilt on every open so a translation file dropped in while the app
            // was running still shows up without a restart of the whole app.
            Languages = BuildLanguageList();
            OnPropertyChanged(nameof(Languages));
            SelectedLanguage = Languages.FirstOrDefault(
                option => string.Equals(option.Tag, settings.LanguageTag, StringComparison.OrdinalIgnoreCase))
                ?? Languages[0];

            Themes = BuildThemeList();
            OnPropertyChanged(nameof(Themes));
            SelectedTheme = Themes.FirstOrDefault(
                option => string.Equals(option.Id, settings.ThemeId, StringComparison.OrdinalIgnoreCase))
                ?? Themes[0];

            Fonts = BuildFontList();
            OnPropertyChanged(nameof(Fonts));

            // A saved family that is not one of the suggestions lands on "Custom…"
            // with the text box filled in, so the user sees what they chose rather
            // than the list silently reverting to the default.
            FontOption? matched = Fonts.FirstOrDefault(
                option => string.Equals(option.Family, settings.FontFamily, StringComparison.OrdinalIgnoreCase));

            if (matched is null && settings.FontFamily.Length > 0)
            {
                CustomFont = settings.FontFamily;
                matched = Fonts[^1];
            }

            SelectedFont = matched ?? Fonts[0];
            OnPropertyChanged(nameof(IsCustomFont));

            OsdTransparencyPercent = Math.Round(settings.OsdTransparency * 100d);
        }
        finally
        {
            _loading = false;
        }

        HasStoredCredential = await _credentials.HasStoredCredentialAsync(ct).ConfigureAwait(true);

        // Read the real OS state rather than trusting the settings file - the user
        // may have removed the entry outside the app.
        StartWithOperatingSystem = await _autostart.IsEnabledAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the language list: "system default" plus everything with translations.
    /// </summary>
    /// <remarks>
    /// Names come from <see cref="LanguageCatalog.NativeName"/> - each language
    /// written in itself - so the list reads correctly whichever language the app
    /// happens to be in, and a language added as a JSON file gets a proper name
    /// without anyone having to translate one.
    /// </remarks>
    private List<LanguageOption> BuildLanguageList()
    {
        var options = new List<LanguageOption>
        {
            new(LanguageCatalog.FollowSystem, _localizer["Config_Language_System"]),
        };

        foreach (string tag in _localizer.AvailableTags())
        {
            try
            {
                options.Add(new LanguageOption(tag, LanguageCatalog.NativeName(CultureInfo.GetCultureInfo(tag))));
            }
            catch (CultureNotFoundException)
            {
                // A JSON file named after a tag this machine's ICU data does not
                // know. Offer it anyway, under its own tag.
                options.Add(new LanguageOption(tag, tag));
            }
        }

        return options;
    }

    /// <summary>
    /// Builds the theme list: "follow the system" plus every theme available.
    /// </summary>
    /// <remarks>
    /// Rebuilt on every open, so a theme file dropped in while the app was running
    /// appears without restarting the whole app.
    /// </remarks>
    private List<ThemeOption> BuildThemeList()
    {
        var options = new List<ThemeOption>
        {
            new(ThemeCatalog.SystemId, _localizer["Config_Theme_System"], Theme: null),
        };

        foreach (Theme theme in _themeStore.All())
        {
            options.Add(new ThemeOption(theme.Id, theme.Name, theme));
        }

        return options;
    }

    /// <summary>
    /// Builds the font list: the platform default, the suggestions, then "Custom…".
    /// </summary>
    /// <remarks>
    /// The suggestions are stacks rather than single families, so one entry covers
    /// Windows, macOS and Linux. Only the first name is shown, because "Segoe UI,
    /// SF Pro Text, Ubuntu, Noto Sans" in a dropdown tells the user nothing they
    /// wanted to know.
    /// </remarks>
    private List<FontOption> BuildFontList()
    {
        var options = new List<FontOption>
        {
            new(FontCatalog.SystemDefault, _localizer["Config_Font_System"]),
        };

        foreach (string stack in FontCatalog.Suggested)
        {
            int comma = stack.IndexOf(',', StringComparison.Ordinal);
            options.Add(new FontOption(stack, comma > 0 ? stack[..comma] : stack));
        }

        options.Add(new FontOption(Family: null, _localizer["Config_Font_Custom"]));
        return options;
    }

    /// <summary>The font family the form currently describes.</summary>
    private string CurrentFontFamily() => SelectedFont is { IsCustom: true }
        ? FontCatalog.Normalize(CustomFont)
        : SelectedFont?.Family ?? FontCatalog.SystemDefault;

    /// <summary>
    /// Repaints the app as soon as a theme control changes, before Save.
    /// </summary>
    /// <remarks>
    /// Same reasoning as the language picker: a theme you cannot see until you
    /// commit to it is a theme you have to pick twice. Nothing is persisted until
    /// Save, so cancelling out reverts on the next start.
    /// </remarks>
    private void PreviewTheme()
    {
        if (_loading || Application.Current is not { } application)
        {
            return;
        }

        Theme theme = ThemeCatalog.Resolve(
            SelectedTheme?.Id, _themeStore.All(), _systemIsDark());

        ThemeApplier.Apply(
            application, theme, CurrentFontFamily(), OsdTransparencyPercent / 100d);
    }

    partial void OnSelectedThemeChanged(ThemeOption value) => PreviewTheme();

    partial void OnOsdTransparencyPercentChanged(double value) => PreviewTheme();

    partial void OnSelectedFontChanged(FontOption value)
    {
        OnPropertyChanged(nameof(IsCustomFont));
        PreviewTheme();
    }

    partial void OnCustomFontChanged(string value)
    {
        // Only repaint once the typed name is usable, so the interface does not
        // flicker back to the default font on every keystroke of "Cascadia Mono".
        if (FontCatalog.IsValidFamily(value) && value.Trim().Length > 0)
        {
            PreviewTheme();
        }
    }

    /// <summary>
    /// Applies the picked language immediately, before Save is pressed.
    /// </summary>
    /// <remarks>
    /// Deliberately instant. Choosing a language you cannot read and then having to
    /// find the Save button in it is a poor trade; seeing the window change as you
    /// pick tells you at once whether you chose the right one. The choice is still
    /// only persisted by Save, so cancelling out reverts on the next start.
    /// </remarks>
    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _localizer.SetCulture(LanguageCatalog.Resolve(
            value.Tag, _localizer.AvailableTags(), CultureInfo.InstalledUICulture));
    }

    /// <summary>Re-reads every composed property after a language change.</summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(null));

        // The list itself contains one translated entry ("System default"), so it
        // has to be rebuilt, keeping the current selection. Under the loading guard,
        // because reassigning the selection would otherwise re-enter SetCulture and
        // this method through it.
        string selected = SelectedLanguage?.Tag ?? LanguageCatalog.FollowSystem;

        _loading = true;
        try
        {
            Languages = BuildLanguageList();
            OnPropertyChanged(nameof(Languages));
            SelectedLanguage = Languages.FirstOrDefault(
                option => string.Equals(option.Tag, selected, StringComparison.OrdinalIgnoreCase))
                ?? Languages[0];
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Stores a pasted token, then clears the input.</summary>
    [RelayCommand]
    private async Task SaveTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(TokenInput))
        {
            StatusMessage = _localizer[CredentialFormat.DescribeKey(CredentialFormatProblem.Empty)];
            return;
        }

        IsBusy = true;
        try
        {
            byte[] token = CredentialFormat.ToBytes(TokenInput);

            // Clear the bound string immediately. StoreAsync takes ownership of the
            // byte[] and zeroes it, so after this line nothing holds the secret.
            TokenInput = string.Empty;

            CredentialFormatProblem problem = await _credentials.StoreAsync(token, ct).ConfigureAwait(true);
            StatusMessage = _localizer[CredentialFormat.DescribeKey(problem)];

            if (problem == CredentialFormatProblem.None)
            {
                HasStoredCredential = true;
                UseClaudeCodeLogin = false;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Forgets any stored token.</summary>
    [RelayCommand]
    private async Task ForgetTokenAsync(CancellationToken ct)
    {
        await _credentials.DeleteAsync(ct).ConfigureAwait(true);
        HasStoredCredential = false;
        StatusMessage = _localizer["Config_Status_TokenRemoved"];
    }

    /// <summary>Makes one real request to prove the credential works.</summary>
    [RelayCommand]
    private async Task TestAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            CredentialSource source = UseClaudeCodeLogin
                ? CredentialSource.ClaudeCodeLogin
                : CredentialSource.ManualToken;

            CredentialTestResult result = await _credentials.TestAsync(source, ct).ConfigureAwait(true);
            StatusMessage = _localizer.Format(result.MessageKey, result.SessionPercent);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Persists the form and applies it to the running app.</summary>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            AppSettings settings = _current() with
            {
                CredentialSource = UseClaudeCodeLogin
                    ? CredentialSource.ClaudeCodeLogin
                    : CredentialSource.ManualToken,
                HasCredential = HasStoredCredential,
                Indicator = UseTaskbarWidget ? IndicatorKind.TaskbarWidget : IndicatorKind.TrayIcon,
                ShowFableInWidget = ShowFableInWidget,
                WidgetUsesThemedCard = !WidgetFollowsSystem,
                ThresholdPercent = ThresholdPercent,
                StartWithOperatingSystem = StartWithOperatingSystem,
                UseFakeProvider = UseFakeProvider,
                DisableAutomaticUpdates = !AutomaticUpdates,
                DisableVelocityAlerts = !VelocityAlerts,
                LanguageTag = SelectedLanguage?.Tag ?? LanguageCatalog.FollowSystem,
                ThemeId = SelectedTheme?.Id ?? ThemeCatalog.SystemId,
                FontFamily = CurrentFontFamily(),
                OsdTransparency = OsdTransparencyPercent / 100d,
                Polling = _current().Polling with
                {
                    BaseInterval = TimeSpan.FromSeconds(Math.Max(PollIntervalSeconds, MinimumPollSeconds)),
                },
            };

            try
            {
                await _autostart.SetEnabledAsync(StartWithOperatingSystem, ct).ConfigureAwait(true);
            }
            catch (AutostartException ex)
            {
                // A refused autostart must not lose the rest of the settings.
                StatusMessage = _localizer[ex.MessageKey];
                StartWithOperatingSystem = false;
                settings = settings with { StartWithOperatingSystem = false };
            }

            await _apply(settings.Normalized()).ConfigureAwait(true);

            if (string.IsNullOrEmpty(StatusMessage)
                || StatusMessage == _localizer["Config_Status_TokenRemoved"])
            {
                StatusMessage = _localizer["Config_Status_Saved"];
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
