using ClaudeStatus.Config;
using ClaudeStatus.Localization;
using ClaudeStatus.Logging;
using ClaudeStatus.Platform;
using ClaudeStatus.Security;
using ClaudeStatus.Theming;
using ClaudeStatus.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeStatus.App.Composition;

/// <summary>
/// Builds the application's service graph.
/// </summary>
/// <remarks>
/// Separate from <see cref="PlatformServices"/> so the OS-specific choices stay
/// in one small, obvious place and everything here is platform-agnostic.
/// </remarks>
public static class AppServices
{
    /// <summary>The HTTP client name for the usage endpoint.</summary>
    public const string UsageHttpClientName = "usage";

    /// <summary>Registers everything the app needs.</summary>
    public static IServiceCollection AddClaudeStatus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPlatformServices();
        services.AddSingleton(TimeProvider.System);
        services.AddClaudeStatusLogging();

        services.AddSingleton<IConfigStore>(provider => new JsonConfigStore(
            provider.GetRequiredService<IPlatformInfo>().ConfigDirectory,
            provider.GetRequiredService<ILogger<JsonConfigStore>>()));

        services.AddSingleton<ISnapshotCache>(provider => new JsonSnapshotCache(
            provider.GetRequiredService<IPlatformInfo>().ConfigDirectory,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<JsonSnapshotCache>>()));

        // A singleton, and it matters: on macOS this source holds the token it read
        // so that polling does not go back to the Keychain, and a fresh instance
        // per resolve would hold nothing and prompt every time.
        services.AddSingleton(p => PlatformServices.CreateClaudeCodeTokenSource(
            loggerFactory: p.GetService<ILoggerFactory>()));

        // One localizer for the whole app, shared by every view model. It has to be
        // a singleton: switching language raises a change on this instance, and any
        // view model holding a different one would keep showing the old language.
        services.AddSingleton(provider => new JsonLanguageStore(
            provider.GetRequiredService<IPlatformInfo>().ConfigDirectory));
        services.AddSingleton<Localizer>(provider => new Localizer(
            resources: null, overrideStore: provider.GetRequiredService<JsonLanguageStore>()));
        services.AddSingleton<ILocalizer>(provider => provider.GetRequiredService<Localizer>());

        services.AddSingleton(provider => new JsonThemeStore(
            provider.GetRequiredService<IPlatformInfo>().ConfigDirectory));

        ConfigureHttp(services);

        services.AddSingleton(provider => new CredentialService(
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<IAccessTokenSource>(),
            tokenSource => CreateRealProvider(provider, tokenSource),
            log: provider.GetRequiredService<ILogger<CredentialService>>()));

        return services;
    }

    /// <summary>
    /// Sets up logging: a rolling file in the config directory, wrapped so that
    /// nothing reaches it unredacted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/manual.md</c> §8 asks for a rolling file and a globally registered
    /// redaction filter. The wrapping order is the whole point: the redactor is
    /// the outermost provider, so there is no path from a log call to the file
    /// that skips it.
    /// </para>
    /// <para>
    /// <b>Console and debug providers are deliberately not added.</b> They would
    /// be a second sink that the redactor does not cover in a published build,
    /// and this app has no console anyway.
    /// </para>
    /// </remarks>
    private static void AddClaudeStatusLogging(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Information);

            // IHttpClientFactory logs four lines per request at Information, which
            // at one poll a minute would bury everything worth reading. Warnings
            // and errors still come through.
            //
            // It also logs request headers - including Authorization - at Trace.
            // The redactor catches those anyway, but keeping this category quiet
            // means the question never arises in a normal build.
            builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

            builder.Services.AddSingleton<ILoggerProvider>(provider =>
            {
                string directory = provider.GetRequiredService<IPlatformInfo>().ConfigDirectory;
                return new RedactingLoggerProvider(new RollingFileLoggerProvider(directory));
            });
        });
    }

    /// <summary>
    /// Configures the one HTTP client this app ever uses.
    /// </summary>
    /// <remarks>
    /// <c>docs/security.md</c> §5: 15 s timeout, no redirect following, and our
    /// own User-Agent. Redirects are off because a redirect must never carry the
    /// <c>Authorization</c> header to another origin.
    /// </remarks>
    private static void ConfigureHttp(IServiceCollection services)
    {
        services.AddHttpClient(UsageHttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    $"ClaudeStatus/{ViewModels.InfoViewModel.Version}");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
            });
    }

    /// <summary>Builds the real provider over a given token source.</summary>
    public static IUsageProvider CreateRealProvider(IServiceProvider provider, IAccessTokenSource tokenSource)
    {
        ArgumentNullException.ThrowIfNull(provider);

        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        return new ClaudeSubscriptionUsageProvider(
            factory.CreateClient(UsageHttpClientName),
            tokenSource,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ClaudeSubscriptionUsageProvider>>());
    }

    /// <summary>
    /// Builds the provider the settings ask for.
    /// </summary>
    /// <remarks>
    /// Rebuilt whenever settings change, because switching the credential source
    /// or turning on fake data has to take effect without a restart.
    /// </remarks>
    public static IUsageProvider CreateProvider(IServiceProvider provider, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.UseFakeProvider)
        {
            return new FakeUsageProvider(
                FakeScenario(), provider.GetRequiredService<TimeProvider>());
        }

        CredentialService credentials = provider.GetRequiredService<CredentialService>();
        return CreateRealProvider(provider, credentials.ResolveTokenSource(settings.CredentialSource));
    }

    /// <summary>The environment variable that picks a fake scenario other than the default.</summary>
    public const string FakeScenarioVariable = "CLAUDESTATUS_FAKE_SCENARIO";

    /// <summary>
    /// Which scripted day the fake provider plays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An environment variable rather than a setting, because it is not a
    /// preference: the scenarios are for development and for the paired
    /// screenshots in <c>docs/screenshots.md</c>, and a user who finds a
    /// "pretend I am nearly out of quota" switch in Config has found a bug, not a
    /// feature. The tick box that turns fake data on at all is the preference.
    /// </para>
    /// <para>
    /// Anything unrecognised - a typo, a number outside the enum - falls back to
    /// <see cref="FakeUsageScenario.Healthy"/> rather than failing to start. This
    /// only ever decides which invented numbers are shown.
    /// </para>
    /// </remarks>
    internal static FakeUsageScenario FakeScenario()
        => FakeScenario(Environment.GetEnvironmentVariable(FakeScenarioVariable));

    /// <inheritdoc cref="FakeScenario()" />
    internal static FakeUsageScenario FakeScenario(string? value)
        => Enum.TryParse(value, ignoreCase: true, out FakeUsageScenario scenario)
            && Enum.IsDefined(scenario)
            ? scenario
            : FakeUsageScenario.Healthy;
}
