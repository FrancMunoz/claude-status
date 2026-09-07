using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using ClaudeStatus.App.Tray;
using ClaudeStatus.App.Views;
using ClaudeStatus.Platform;
using ClaudeStatus.Security;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Proves every window's AXAML actually loads and its bindings resolve.
/// </summary>
/// <remarks>
/// A broken binding path or a missing style resource compiles cleanly and fails
/// at load, so these are the tests that stop the app shipping with a window that
/// throws the moment someone opens it.
/// </remarks>
[Collection(HeadlessTests.Name)]
/// <remarks>
/// The <c>HeadlessAppFixture</c> parameter is what makes xUnit build the shared
/// Avalonia session before any test in this collection runs. It is not used
/// directly: <c>Invoke</c> reaches the dispatcher statically.
/// </remarks>
[SuppressMessage("Performance", "CA1801:Review unused parameters",
    Justification = "The fixture parameter forces xUnit to start the Avalonia session.")]
public class WindowLoadTests(HeadlessAppFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot() => new(
        Session: UsageWindow.Create(29d, Now.AddHours(2)),
        Week: UsageWindow.Create(58d, Now.AddDays(2)),
        WeekFable: UsageWindow.Create(88d, Now.AddDays(2)),
        OtherWindows: new Dictionary<string, UsageWindow>(),
        FetchedAt: Now,
        IsStale: false);

    [Fact]
    public void The_details_window_loads_and_binds()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            var monitor = new UsageMonitor(
                new FakeUsageProvider(FakeUsageScenario.Healthy, new FakeTimeProvider(Now)),
                new PollingOptions(),
                new FakeTimeProvider(Now));

            using var viewModel = new DetailsViewModel(
                monitor, () => new AppSettings(), TestLocalizer.English(), new FakeTimeProvider(Now));
            viewModel.Apply(Snapshot());

            var window = new DetailsWindow { DataContext = viewModel };
            window.Should().NotBeNull();
            window.DataContext.Should().BeSameAs(viewModel);

            monitor.Dispose();
        });
    }

    [Fact]
    public void The_info_window_loads_and_binds()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            var viewModel = new InfoViewModel(
                new StubPlatformInfo(),
                new FakeUsageProvider(FakeUsageScenario.Healthy, new FakeTimeProvider(Now)),
                TestLocalizer.English());

            var window = new InfoWindow { DataContext = viewModel };
            window.DataContext.Should().BeSameAs(viewModel);
        });
    }

    [Fact]
    public void The_report_window_loads_and_binds()
    {
        // Its template is the most complex in the app - an ItemsControl of nested
        // grids - so a broken binding here is the one most likely to go unnoticed.
        HeadlessAppFixture.Invoke(() =>
        {
            var clock = new FakeTimeProvider(Now);
            var monitor = new UsageMonitor(
                new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

            using var viewModel = new ReportViewModel(
                monitor,
                new FakeUsageProvider(FakeUsageScenario.Healthy, clock),
                new StubPlatformInfo(),
                TestLocalizer.English(),
                clock);

            viewModel.Apply(Snapshot());

            var window = new ReportWindow { DataContext = viewModel };
            window.DataContext.Should().BeSameAs(viewModel);
            viewModel.Rows.Should().NotBeEmpty("the template has nothing to render otherwise");

            monitor.Dispose();
        });
    }

    [Fact]
    public void The_config_window_loads_and_binds()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            var window = new ConfigWindow { DataContext = BuildConfigViewModel() };
            window.DataContext.Should().NotBeNull();
        });
    }

    [Fact]
    public void The_shared_styles_resolve()
    {
        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        // A typo in a Selector or a missing StyleInclude only shows up here.
        HeadlessAppFixture.Invoke(() =>
        {
            Application.Current.Should().NotBeNull();
            Application.Current!.Styles.Should().NotBeEmpty();
        });
    }

    [Theory]
    [InlineData(IndicatorMode.SessionPercent)]
    [InlineData(IndicatorMode.WeekPercent)]
    [InlineData(IndicatorMode.WeekFablePercent)]
    [InlineData(IndicatorMode.Ring)]
    public void The_tray_icon_renders_in_every_mode(IndicatorMode mode)
    {
        HeadlessAppFixture.Invoke(() =>
        {
            using var bitmap = TrayIconRenderer.Render(Snapshot(), mode, ThresholdState.Normal);

            bitmap.PixelSize.Width.Should().Be(TrayIconRenderer.NominalSize * TrayIconRenderer.Scale);
            bitmap.PixelSize.Height.Should().Be(TrayIconRenderer.NominalSize * TrayIconRenderer.Scale);
        });
    }

    [Fact]
    public void The_tray_icon_renders_with_no_data_at_all()
    {
        // The very first paint happens before any fetch has completed.
        HeadlessAppFixture.Invoke(() =>
        {
            using var bitmap = TrayIconRenderer.Render(null, IndicatorMode.SessionPercent, ThresholdState.Unknown);

            bitmap.Should().NotBeNull();
        });
    }

    [Fact]
    public void The_tray_icon_renders_a_missing_Fable_window()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            UsageSnapshot snapshot = Snapshot() with { WeekFable = null };

            using var bitmap = TrayIconRenderer.Render(
                snapshot, IndicatorMode.WeekFablePercent, ThresholdState.Unknown);

            bitmap.Should().NotBeNull();
        });
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(100d)]
    public void The_ring_renders_at_both_extremes(double percent)
    {
        // Zero must still draw the track, and a full sweep must not trip the
        // large-arc maths.
        HeadlessAppFixture.Invoke(() =>
        {
            UsageSnapshot snapshot = Snapshot() with { Session = UsageWindow.Create(percent, Now) };

            using var bitmap = TrayIconRenderer.Render(snapshot, IndicatorMode.Ring, ThresholdState.Normal);

            bitmap.Should().NotBeNull();
        });
    }

    private static ClaudeStatus.Security.CredentialService BuildCredentialService()
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
            new JsonConfigStore(Path.Combine(Path.GetTempPath(), "claudestatus-uitests")),
            TestLocalizer.English(),
            new ClaudeStatus.Localization.JsonLanguageStore(
                Path.Combine(Path.GetTempPath(), "claudestatus-uitests")),
            new ClaudeStatus.Theming.JsonThemeStore(
                Path.Combine(Path.GetTempPath(), "claudestatus-uitests")),
            () => true,
            () => new AppSettings(),
            _ => Task.CompletedTask);

    private sealed class StubPlatformInfo : IPlatformInfo
    {
        public PlatformKind Kind => PlatformKind.Windows;

        public string OperatingSystemName => "Test OS";

        public string ConfigDirectory => Path.Combine(Path.GetTempPath(), "claudestatus-uitests");

        public TraySupport TraySupport => TraySupport.Available;

        public string? ExecutablePath => Environment.ProcessPath;
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
