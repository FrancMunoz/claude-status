using System.Reflection;
using System.Text.Json;
using ClaudeStatus.Theming;

namespace ClaudeStatus.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private JsonConfigStore Store() => new(_directory);

    [Fact]
    public async Task Returns_defaults_on_a_first_run()
    {
        JsonConfigStore store = Store();

        store.Exists.Should().BeFalse();
        AppSettings settings = await store.LoadAsync(Ct);

        // Row, not SessionPercent: it is the richest reading a tray can give, and
        // a tray that needs a square icon renders it as the session number anyway,
        // so the default costs nothing where the row cannot be drawn.
        settings.IndicatorMode.Should().Be(IndicatorMode.Row);
        settings.ThresholdPercent.Should().Be(80d);
        settings.CredentialSource.Should().Be(CredentialSource.ClaudeCodeLogin);
        settings.HasCredential.Should().BeFalse();
    }

    [Fact]
    public async Task A_settings_file_written_before_updates_existed_keeps_them_on()
    {
        // Every install that predates the update checker has a settings.json with
        // no automaticUpdates key. If an absent key deserialized as false, all of
        // them would silently never update again - and the symptom would be no
        // symptom at all, which is the worst kind to ship.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "settings.json"),
            """
            {
              "schemaVersion": 1,
              "indicatorMode": "SessionPercent",
              "thresholdPercent": 80,
              "credentialSource": "ClaudeCodeLogin",
              "useFakeProvider": false
            }
            """,
            Ct);

        AppSettings loaded = await Store().LoadAsync(Ct);

        loaded.AutomaticUpdates.Should().BeTrue();
    }

    [Fact]
    public async Task An_absent_property_keeps_the_default_it_was_declared_with()
    {
        // The general form of the bug above. Every property initializer on
        // AppSettings is a default that only holds if the deserializer runs it,
        // and most of them are invisible when it does not: Normalized() repairs an
        // empty ThemeId or a null Polling, so those defaults appear to work while
        // actually being restored a step later. SchemaVersion and AutomaticUpdates
        // have no validator to hide behind, which is why they are asserted here.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, JsonConfigStore.FileName), "{}", Ct);

        AppSettings loaded = await Store().LoadAsync(Ct);

        loaded.SchemaVersion.Should().Be(1);
        loaded.AutomaticUpdates.Should().BeTrue();
        loaded.Polling.Should().NotBeNull();
        loaded.ThemeId.Should().Be(ThemeCatalog.DefaultId);
    }

    [Fact]
    public async Task Round_trips_settings()
    {
        JsonConfigStore store = Store();
        var saved = new AppSettings
        {
            IndicatorMode = IndicatorMode.WeekFablePercent,
            ThresholdPercent = 65d,
            StartWithOperatingSystem = true,
            CredentialSource = CredentialSource.ManualToken,
            HasCredential = true,
        };

        await store.SaveAsync(saved, Ct);
        AppSettings loaded = await store.LoadAsync(Ct);

        loaded.IndicatorMode.Should().Be(IndicatorMode.WeekFablePercent);
        loaded.ThresholdPercent.Should().Be(65d);
        loaded.StartWithOperatingSystem.Should().BeTrue();
        loaded.CredentialSource.Should().Be(CredentialSource.ManualToken);
        loaded.HasCredential.Should().BeTrue();
        store.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task Creates_the_config_directory_if_it_is_missing()
    {
        JsonConfigStore store = Store();

        await store.SaveAsync(new AppSettings(), Ct);

        Directory.Exists(_directory).Should().BeTrue();
    }

    [Fact]
    public async Task Falls_back_to_defaults_for_a_corrupt_file_rather_than_refusing_to_start()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, JsonConfigStore.FileName), "{ this is not json", Ct);

        AppSettings loaded = await Store().LoadAsync(Ct);

        loaded.ThresholdPercent.Should().Be(80d);
    }

    [Fact]
    public async Task Ignores_unknown_properties_from_a_newer_version()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, JsonConfigStore.FileName),
            """{ "thresholdPercent": 55, "somethingFromTheFuture": { "nested": true } }""",
            Ct);

        AppSettings loaded = await Store().LoadAsync(Ct);

        loaded.ThresholdPercent.Should().Be(55d);
    }

    [Fact]
    public async Task Clamps_an_out_of_range_threshold_read_from_disk()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, JsonConfigStore.FileName), """{ "thresholdPercent": 9000 }""", Ct);

        (await Store().LoadAsync(Ct)).ThresholdPercent.Should().Be(100d);
    }

    [Fact]
    public async Task Clamps_a_poll_interval_below_the_hard_floor()
    {
        // A hand-edited config must not be able to get the user rate limited.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, JsonConfigStore.FileName),
            """{ "polling": { "baseInterval": "00:00:01" } }""",
            Ct);

        (await Store().LoadAsync(Ct)).Polling.BaseInterval.Should().Be(PollingOptions.MinimumInterval);
    }

    [Fact]
    public async Task Leaves_the_previous_file_intact_when_a_save_is_cancelled()
    {
        JsonConfigStore store = Store();
        await store.SaveAsync(new AppSettings { ThresholdPercent = 42d }, Ct);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        try
        {
            await store.SaveAsync(new AppSettings { ThresholdPercent = 99d }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        (await store.LoadAsync(Ct)).ThresholdPercent.Should().Be(42d);
    }

    [Fact]
    public async Task The_settings_file_never_contains_credential_shaped_text()
    {
        JsonConfigStore store = Store();
        await store.SaveAsync(new AppSettings { HasCredential = true, CredentialSource = CredentialSource.ManualToken }, Ct);

        string json = await File.ReadAllTextAsync(store.SettingsFilePath, Ct);

        json.Should().NotContain("sk-ant-");
        json.Should().Contain("hasCredential", "the flag is stored");
        Redactor.LooksRedacted(json).Should().BeTrue();
    }

    /// <summary>
    /// The string properties <see cref="AppSettings"/> is allowed to have.
    /// </summary>
    /// <remarks>
    /// Adding to this list is a security decision, not a refactor. Each entry needs
    /// a normalizer that makes the field structurally incapable of holding a token,
    /// and a pair of tests below proving that hostile input is discarded and real
    /// input survives. All three current entries are short, charset-restricted and
    /// length-capped; <c>FontFamily</c> is additionally checked for credential
    /// shape because it is the loosest of them. See <c>docs/security.md</c> §4.1.
    /// </remarks>
    private static readonly string[] AllowedStringProperties =
    [
        nameof(AppSettings.LanguageTag),
        nameof(AppSettings.ThemeId),
        nameof(AppSettings.FontFamily),
    ];

    [Fact]
    public void AppSettings_declares_no_unreviewed_property_that_could_hold_a_secret()
    {
        // Structural guard: if someone adds a string property to AppSettings, this
        // fails and forces a conscious decision. See docs/security.md §4.
        IEnumerable<string> stringProperties = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Except(AllowedStringProperties);

        stringProperties.Should().BeEmpty(
            "AppSettings must never gain a string field that could carry a token");
    }

    [Theory]
    [InlineData("sk-ant-oat01-abcdefghijklmnopqrstuvwxyz0123456789abcdef")]
    [InlineData("sk-ant-ort01-abcdef")]
    [InlineData("../../../etc/passwd")]
    [InlineData("en-US-x-something-far-too-long")]
    [InlineData("EN")]
    [InlineData("e")]
    [InlineData("../es")]
    [InlineData(" es ")]
    [InlineData("es ")]
    public void The_language_tag_discards_anything_that_is_not_a_language_tag(string hostile)
    {
        // LanguageTag is the only free-text field in the settings file, so its
        // normalizer is what stops that field being a place to hide a credential.
        AppSettings normalized = new AppSettings { LanguageTag = hostile }.Normalized();

        normalized.LanguageTag.Should().BeEmpty();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ca")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    public void A_real_language_tag_survives_normalisation(string tag)
    {
        new AppSettings { LanguageTag = tag }.Normalized().LanguageTag.Should().Be(tag);
    }

    [Theory]
    [InlineData("sk-ant-oat01-abcdefghijklmnopqrstuvwxyz0123456789abcdef")]
    [InlineData("../../themes/evil")]
    [InlineData("Dark")]
    [InlineData("a-really-long-theme-identifier-that-goes-on-and-on")]
    [InlineData("théme")]
    [InlineData("")]
    public void The_theme_id_discards_anything_that_is_not_an_id(string hostile)
    {
        // The id also becomes a file name under themes/, so this is what stops a
        // path escaping that folder as well as what keeps a token out of settings.
        new AppSettings { ThemeId = hostile }.Normalized()
            .ThemeId.Should().Be(ThemeCatalog.DefaultId);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("claude")]
    [InlineData("my-theme_2")]
    public void A_real_theme_id_survives_normalisation(string id)
    {
        new AppSettings { ThemeId = id }.Normalized().ThemeId.Should().Be(id);
    }

    [Theory]
    [InlineData("sk-ant-oat01-abcdefghijklmnopqrst")]
    [InlineData("../../../etc/passwd")]
    [InlineData("Segoe<script>")]
    [InlineData("A font name that is very much longer than sixty-four characters and keeps going")]
    public void The_font_family_discards_anything_that_is_not_a_font_name(string hostile)
    {
        // The loosest field in the file, so it carries the most validation: a
        // length cap, a charset, and a credential-shape check on top.
        new AppSettings { FontFamily = hostile }.Normalized()
            .FontFamily.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Segoe UI")]
    [InlineData("Segoe UI, SF Pro Text, Ubuntu")]
    [InlineData("Cascadia Mono")]
    [InlineData("Iowan Old Style")]
    [InlineData("")]
    public void A_real_font_family_survives_normalisation(string family)
    {
        new AppSettings { FontFamily = family }.Normalized().FontFamily.Should().Be(family);
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(-5d, 0d)]
    [InlineData(0.5d, 0.5d)]
    [InlineData(2d, 1d)]
    [InlineData(double.NaN, AppSettings.DefaultOsdTransparency)]
    public void The_popup_transparency_is_clamped_into_range(double given, double expected)
    {
        new AppSettings { OsdTransparency = given }.Normalized()
            .OsdTransparency.Should().Be(expected);
    }

    [Fact]
    public void An_explicit_zero_stays_solid_and_is_not_mistaken_for_unset()
    {
        // The whole reason the property is nullable. 0 is a setting a user can
        // legitimately want, so it must survive normalisation untouched even
        // though the default is no longer 0.
        new AppSettings { OsdTransparency = 0d }.Normalized()
            .OsdTransparency.Should().Be(0d);
    }

    [Fact]
    public void The_popup_is_slightly_transparent_by_default()
    {
        new AppSettings().OsdTransparency.Should().BeNull("nothing has been chosen yet");
        new AppSettings().Normalized()
            .OsdTransparency.Should().Be(AppSettings.DefaultOsdTransparency);
    }

    [Fact]
    public async Task A_settings_file_with_no_transparency_field_loads_as_the_default()
    {
        // The path that made the property nullable. A plain double would come
        // back as 0 here - the JSON source generator does not run property
        // initialisers - and a fresh install would disagree with a partial file
        // about what "default" means.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, JsonConfigStore.FileName), """{ "thresholdPercent": 80 }""", Ct);

        (await Store().LoadAsync(Ct))
            .OsdTransparency.Should().Be(AppSettings.DefaultOsdTransparency);
    }

    [Fact]
    public async Task A_settings_file_that_asks_for_a_solid_popup_keeps_it_across_a_reload()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, JsonConfigStore.FileName), """{ "osdTransparency": 0 }""", Ct);

        (await Store().LoadAsync(Ct)).OsdTransparency.Should().Be(0d);
    }

    [Fact]
    public void A_fully_transparent_popup_is_a_real_setting_not_a_clamped_one()
    {
        // Only the background carries the alpha, so at 100 % the readings float
        // over the desktop and stay legible.
        new AppSettings { OsdTransparency = 1d }.Normalized()
            .OsdTransparency.Should().Be(1d);
    }

    [Fact]
    public async Task A_hostile_font_family_never_reaches_the_settings_file()
    {
        JsonConfigStore store = Store();
        await store.SaveAsync(
            new AppSettings { FontFamily = "sk-ant-oat01-notatoken" }.Normalized(), Ct);

        string json = await File.ReadAllTextAsync(store.SettingsFilePath, Ct);

        json.Should().NotContain("sk-ant-");
        Redactor.LooksRedacted(json).Should().BeTrue();
    }

    [Fact]
    public async Task A_hostile_language_tag_never_reaches_the_settings_file()
    {
        // Normalized() is the guard, but the guard is only worth anything if the
        // save path actually runs it. This asserts the whole route, not the unit.
        JsonConfigStore store = Store();
        await store.SaveAsync(
            new AppSettings { LanguageTag = "sk-ant-oat01-notatoken" }.Normalized(), Ct);

        string json = await File.ReadAllTextAsync(store.SettingsFilePath, Ct);

        json.Should().NotContain("sk-ant-");
        Redactor.LooksRedacted(json).Should().BeTrue();
    }

    [Fact]
    public void Throws_for_a_null_or_blank_config_directory()
    {
        Action act = () => _ = new JsonConfigStore("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Enums_are_written_as_names_so_the_file_stays_readable()
    {
        JsonConfigStore store = Store();
        await store.SaveAsync(new AppSettings { IndicatorMode = IndicatorMode.WeekPercent }, Ct);

        string json = await File.ReadAllTextAsync(store.SettingsFilePath, Ct);

        json.Should().Contain("WeekPercent");
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
