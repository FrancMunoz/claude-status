using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClaudeStatus.App.Theming;
using ClaudeStatus.App.Tray;
using ClaudeStatus.App.Views;
using ClaudeStatus.Platform;
using ClaudeStatus.Security;
using ClaudeStatus.Theming;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Rebuilds the screenshots the readme shows.
/// </summary>
/// <remarks>
/// <para>
/// The images are committed binaries, so this does not run in CI. It runs when
/// somebody asks for it:
/// </para>
/// <code>
/// $env:CLAUDESTATUS_WRITE_SCREENSHOTS = "$PWD/docs/screenshots"
/// dotnet test tests/ClaudeStatus.App.Tests --filter-method '*Regenerates_the_readme*'
/// </code>
/// <para>
/// The path has to be absolute, for the same reason it does in
/// <see cref="IconGenerator"/>: the test host runs with its own output directory
/// as the working directory.
/// </para>
/// <para>
/// It exists because the readme's windows were previously captured by a script
/// nobody kept, which made a change to the mark or a colour into an afternoon of
/// lining screenshots up by hand. Every image here is a real render of the real
/// window through the real theme - the same headless Skia session the rest of
/// these tests assert pixels with - so the only thing that can drift between the
/// readme and the app is this file.
/// </para>
/// <para>
/// It deliberately does not cover <c>macos-menu-bar.png</c>. That one is a
/// photograph of a menu bar we do not draw: the bar, its spacing and the system
/// icons beside ours belong to macOS, and a headless Windows render of the item
/// alone would be a picture of something the reader never sees.
/// </para>
/// </remarks>
[Collection(HeadlessTests.Name)]
/// <remarks>
/// The <c>HeadlessAppFixture</c> parameter is what makes xUnit build the shared
/// Avalonia session before this runs; <c>Invoke</c> reaches the dispatcher
/// statically.
/// </remarks>
[SuppressMessage("Performance", "CA1801:Review unused parameters",
    Justification = "The fixture parameter forces xUnit to start the Avalonia session.")]
public class ScreenshotGenerator(HeadlessAppFixture fixture)
{
    /// <summary>The instant every window is rendered at, so the countdowns are stable.</summary>
    /// <remarks>
    /// Rendering at <c>DateTimeOffset.Now</c> would rewrite "resets in 2h 37m" and
    /// every absolute date in the report on each run, making a regeneration a diff
    /// whether anything changed or not. The value is the one the shipped images
    /// were taken at, so re-running this changes only what actually moved.
    /// </remarks>
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The Windows 11 taskbar, dark and light.</summary>
    /// <remarks>
    /// Sampled from the shipped images rather than derived: the widget is
    /// re-parented into a strip we do not paint, and these are what it actually
    /// sits on. They are the backdrop for the widget shots only - the popup is a
    /// window in its own right and composites onto black like any other.
    /// </remarks>
    private static readonly Color DarkTaskbar = Color.FromRgb(0x20, 0x20, 0x20);

    private static readonly Color LightTaskbar = Color.FromRgb(0xF3, 0xF3, 0xF3);

    /// <summary>The themes the readme shows off, in the order it shows them.</summary>
    /// <remarks>
    /// Each carries the taskbar its widget is shown on, which is not a property of
    /// the theme: the strip belongs to the reader, and a gallery of three shots of
    /// the same dark strip would say nothing about how the widget copes with the
    /// light one. Matcha draws the light taskbar for that reason alone.
    /// </remarks>
    private static readonly (string Id, TrayBackground Taskbar)[] Gallery =
    [
        ("claude", TrayBackground.Dark),
        ("nebula", TrayBackground.Dark),
        ("matcha", TrayBackground.Light),
    ];

    private static string? OutputDirectory
        => Environment.GetEnvironmentVariable("CLAUDESTATUS_WRITE_SCREENSHOTS");

    [Fact]
    public void Regenerates_the_readme_screenshots()
    {
        string? root = OutputDirectory;
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(root),
            "Set CLAUDESTATUS_WRITE_SCREENSHOTS to docs/screenshots to rewrite the images.");

        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        HeadlessAppFixture.Invoke(() =>
        {
            Directory.CreateDirectory(Path.Combine(root!, "themes"));
            Directory.CreateDirectory(Path.Combine(root!, "report"));

            // The popup, once per gallery theme. The readme shows the Claude one
            // twice - as the tour's opening image and as the first of the three -
            // so window-details.png and themes/claude.png are the same render.
            foreach ((string id, _) in Gallery)
            {
                Save(RenderDetails(Theme(id)), Path.Combine(root!, "themes", $"{id}.png"));
            }

            File.Copy(
                Path.Combine(root!, "themes", "claude.png"),
                Path.Combine(root!, "window-details.png"),
                overwrite: true);

            Save(RenderConfig(Theme("claude")), Path.Combine(root!, "window-config.png"));
            Save(RenderReport(Theme("claude")), Path.Combine(root!, "report", "report-en.png"));

            // The widget: the themed card, the two blends, and the Fable column.
            Save(
                RenderWidget(Theme("claude"), TrayBackground.Dark, followSystem: false, showFable: false),
                Path.Combine(root!, "taskbar-widget.png"));
            Save(
                RenderWidget(Theme("claude"), TrayBackground.Dark, followSystem: true, showFable: false),
                Path.Combine(root!, "taskbar-widget-blend.png"));
            Save(
                RenderWidget(Theme("claude"), TrayBackground.Light, followSystem: true, showFable: false),
                Path.Combine(root!, "taskbar-widget-blend-light.png"));
            Save(
                RenderWidget(Theme("claude"), TrayBackground.Dark, followSystem: false, showFable: true),
                Path.Combine(root!, "taskbar-widget-fable.png"));

            foreach ((string id, TrayBackground taskbar) in Gallery)
            {
                Save(
                    RenderWidget(Theme(id), taskbar, followSystem: false, showFable: false),
                    Path.Combine(root!, "themes", $"widget-{id}.png"));
            }
        });
    }

    private static Theme Theme(string id) => ThemeCatalog.BuiltIn.Single(t => t.Id == id);

    /// <summary>The reading every window is rendered from.</summary>
    /// <remarks>
    /// Session under the threshold, the week over half, Fable past it - one
    /// window in each of the three states the bars can be in, so a screenshot
    /// shows the normal, the busy and the alert colour at once.
    /// </remarks>
    private static UsageSnapshot Snapshot() => new(
        Session: UsageWindow.Create(29d, Now.AddHours(2).AddMinutes(37)),
        Week: UsageWindow.Create(58d, Now.AddDays(2)),
        WeekFable: UsageWindow.Create(88d, Now.AddDays(2)),
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: Now,
        IsStale: false);

    /// <summary>
    /// Renders a window and hands back its pixels.
    /// </summary>
    /// <remarks>
    /// <c>RunJobs</c> is what makes this a picture of a laid-out window rather
    /// than of an empty one: <c>Show</c> queues the layout pass, and the capture
    /// would otherwise race it.
    /// </remarks>
    private static WriteableBitmap Render(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException(
                $"{window.GetType().Name} rendered no frame; is the headless drawing backend on?");

        window.Close();
        return frame;
    }

    private static WriteableBitmap RenderDetails(Theme theme)
    {
        // Fully opaque, which is not the app's default. The default 10 % lets the
        // black the headless session clears to show through, and the readme would
        // then show a popup a shade darker than the theme it is illustrating.
        ThemeApplier.Apply(Application.Current!, theme, string.Empty, osdTransparency: 0d);

        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new DetailsViewModel(
            monitor, () => new AppSettings(), TestLocalizer.English(), clock);
        viewModel.Apply(Snapshot());

        return Render(new DetailsWindow { DataContext = viewModel });
    }

    private static WriteableBitmap RenderConfig(Theme theme)
    {
        ThemeApplier.Apply(Application.Current!, theme, string.Empty, osdTransparency: 0d);

        return Render(new ConfigWindow { DataContext = BuildConfigViewModel() });
    }

    private static WriteableBitmap RenderReport(Theme theme)
    {
        ThemeApplier.Apply(Application.Current!, theme, string.Empty, osdTransparency: 0d);

        var clock = new FakeTimeProvider(Now);
        using var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        using var viewModel = new ReportViewModel(
            monitor,
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock),
            new StubPlatformInfo(),
            TestLocalizer.English(),
            clock);
        viewModel.Apply(Snapshot());

        return Render(new ReportWindow { DataContext = viewModel });
    }

    private static WriteableBitmap RenderWidget(
        Theme theme, TrayBackground taskbar, bool followSystem, bool showFable)
    {
        ThemeApplier.Apply(Application.Current!, theme, string.Empty, osdTransparency: 0d);

        var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
        viewModel.Configure(80d, showFable: showFable, followSystem: followSystem);
        viewModel.Update(Snapshot(), IndicatorAlert.None, Now);

        var window = new TaskbarWidgetWindow
        {
            DataContext = viewModel,

            // The strip it is re-parented into. Blended, the widget draws no card
            // at all, so this is not a backdrop behind the subject - it is the
            // subject's background, and getting it wrong would show white text on
            // black in a shot captioned "light taskbar".
            Background = new SolidColorBrush(
                taskbar == TrayBackground.Light ? LightTaskbar : DarkTaskbar),
        };

        TaskbarInk.Apply(window.Resources, taskbar);
        return Render(window);
    }

    private static void Save(WriteableBitmap bitmap, string path)
    {
        using (bitmap)
        {
            using var file = File.Create(path);
            bitmap.Save(file, new PngBitmapEncoderOptions());
        }
    }

    private static CredentialService BuildCredentialService()
        => new(
            new NullSecretStore(),
            new NullTokenSource(),
            _ => new FakeUsageProvider(FakeUsageScenario.Healthy, new FakeTimeProvider(Now)));

    private static ConfigViewModel BuildConfigViewModel()
        => new(
            BuildCredentialService(),
            new StubAutostart(),
            new StubPlatformInfo(),
            new UnsupportedTaskbarHost(),
            new JsonConfigStore(Path.Combine(Path.GetTempPath(), "claudestatus-shotgen")),
            TestLocalizer.English(),
            new ClaudeStatus.Localization.JsonLanguageStore(
                Path.Combine(Path.GetTempPath(), "claudestatus-shotgen")),
            new JsonThemeStore(Path.Combine(Path.GetTempPath(), "claudestatus-shotgen")),
            () => true,
            () => new AppSettings(),
            _ => Task.CompletedTask);

    private sealed class StubPlatformInfo : IPlatformInfo
    {
        public PlatformKind Kind => PlatformKind.Windows;

        public string OperatingSystemName => "Test OS";

        public string ConfigDirectory => Path.Combine(Path.GetTempPath(), "claudestatus-shotgen");

        public TraySupport TraySupport => TraySupport.Available;

        public string? ExecutablePath => Environment.ProcessPath;

        public bool SupportsInlineTrayText => false;

        public bool TrayIsAtTop => false;
    }

    private sealed class StubAutostart : IAutostart
    {
        public string DescriptionKey => "Autostart_WindowsRegistry";

        public bool IsSupported => true;

        public Task<bool> IsEnabledAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullSecretStore : ISecretStore
    {
        public string DescriptionKey => "Store_WindowsDpapi";

        public bool IsHardened => true;

        public Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
            => Task.CompletedTask;

        public Task<byte[]?> RetrieveAsync(string key, CancellationToken ct)
            => Task.FromResult<byte[]?>(null);

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullTokenSource : IAccessTokenSource
    {
        public string DescriptionKey => "TokenSource_ClaudeCodeLogin";

        public Task<byte[]?> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult<byte[]?>(null);
    }
}
