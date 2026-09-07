using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using ClaudeStatus.App.Branding;
using ClaudeStatus.App.Theming;
using ClaudeStatus.App.Tray;
using ClaudeStatus.App.Views;
using ClaudeStatus.Platform;
using ClaudeStatus.Security;
using ClaudeStatus.Theming;
using Microsoft.Extensions.Time.Testing;
using Shapes = Avalonia.Controls.Shapes;

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

    [Fact]
    public void The_taskbar_widget_window_loads_and_draws_the_Claude_mark()
    {
        // An {x:Static} that fails to resolve leaves Data null, and the widget
        // then loads perfectly with nothing drawn where the mark should be. The
        // bounds check is what separates "resolved" from "resolved to an empty
        // shape", which is what an unparsable outline would look like.
        HeadlessAppFixture.Invoke(() =>
        {
            var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
            viewModel.Update(Snapshot(), IndicatorAlert.None, Now);

            var window = new TaskbarWidgetWindow { DataContext = viewModel };

            Shapes.Path mark = window.GetLogicalDescendants()
                .OfType<Shapes.Path>()
                .Single(p => p.Classes.Contains("widgetMark"));

            mark.Data.Should().BeSameAs(AppMark.Geometry, "one outline, drawn everywhere");
            mark.Data!.Bounds.Width.Should().BeGreaterThan(0d, "an unparsed path is an empty shape");
            mark.Data.Bounds.Height.Should().BeGreaterThan(0d, "an unparsed path is an empty shape");
        });
    }

    [Fact]
    public void The_hover_card_loads_and_leads_with_the_mark()
    {
        // The card the widget shows on hover. Its heading is the details popup's
        // heading, so it draws the details popup's mark beside it; it used to be
        // a 7px dot, which read as a bullet rather than as the application.
        HeadlessAppFixture.Invoke(() =>
        {
            var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
            viewModel.Update(Snapshot(), IndicatorAlert.None, Now);

            var window = new TaskbarHoverWindow { DataContext = viewModel };

            Shapes.Path mark = window.GetLogicalDescendants().OfType<Shapes.Path>().Single();
            mark.Data.Should().BeSameAs(AppMark.Geometry, "one outline, drawn everywhere");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_mark_is_inked_by_the_same_choice_as_the_numbers_beside_it(bool followSystem)
    {
        // Blended, the mark matches the taskbar ink the values use; on the
        // themed card it takes the primary. Both come from styles keyed on the
        // .system class, so the risk is a selector that never matches and a mark
        // that silently stays one colour in both modes.
        HeadlessAppFixture.Invoke(() =>
        {
            // The headless app boots with no theme applied, so the .system-off case
            // would resolve Theme.Primary to nothing and pass vacuously.
            ThemeApplier.Apply(Application.Current!, ThemeCatalog.Dark, string.Empty, 0d);

            var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
            viewModel.Configure(80d, showFable: false, followSystem: followSystem);
            viewModel.Update(Snapshot(), IndicatorAlert.None, Now);

            var window = new TaskbarWidgetWindow { DataContext = viewModel };
            TaskbarInk.Apply(window.Resources, TrayBackground.Dark);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Shapes.Path mark = window.GetLogicalDescendants()
                .OfType<Shapes.Path>()
                .Single(p => p.Classes.Contains("widgetMark"));

            Rgb primary = ThemeCatalog.Dark.Primary;
            Color expected = followSystem
                ? TaskbarInk.InkFor(TrayBackground.Dark)
                : Color.FromRgb(primary.R, primary.G, primary.B);

            mark.Fill.Should().BeAssignableTo<ISolidColorBrush>();
            ((ISolidColorBrush)mark.Fill!).Color.Should().Be(expected);

            window.Close();
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_the_themed_card_carries_the_primary_outline_and_its_padding(bool followSystem)
    {
        // The themed card is outlined in the primary and padded to enclose it.
        // Blended keeps the tighter padding, because there is no outline there to
        // enclose and the strip would otherwise drift away from the tray for no
        // visible reason. The .system padding setter reads as redundant with the
        // one above it and is not; this is what says so.
        HeadlessAppFixture.Invoke(() =>
        {
            ThemeApplier.Apply(Application.Current!, ThemeCatalog.Dark, string.Empty, 0d);

            var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
            viewModel.Configure(80d, showFable: false, followSystem: followSystem);
            viewModel.Update(Snapshot(), IndicatorAlert.None, Now);

            var window = new TaskbarWidgetWindow { DataContext = viewModel };
            TaskbarInk.Apply(window.Resources, TrayBackground.Dark);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Border card = window.GetLogicalDescendants()
                .OfType<Border>()
                .Single(b => b.Classes.Contains("widget"));

            if (followSystem)
            {
                card.BorderThickness.Should().Be(default(Thickness), "blending draws no card");
                card.Padding.Should().Be(new Thickness(10, 0));
            }
            else
            {
                Rgb primary = ThemeCatalog.Dark.Primary;

                card.BorderThickness.Should().Be(new Thickness(1));
                card.BorderBrush.Should().BeAssignableTo<ISolidColorBrush>();
                ((ISolidColorBrush)card.BorderBrush!).Color
                    .Should().Be(Color.FromRgb(primary.R, primary.G, primary.B));
                card.Padding.Should().Be(new Thickness(14, 4));
                card.CornerRadius.TopLeft.Should().BeGreaterThan(0d, "the outline is rounded");
            }

            window.Close();
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
