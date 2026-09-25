using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClaudeStatus.App.Theming;
using ClaudeStatus.App.ViewModels;
using ClaudeStatus.App.Views;
using ClaudeStatus.Sessions;
using ClaudeStatus.Theming;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// The working badge's pulse: what it animates, and what it must leave alone.
/// </summary>
/// <remarks>
/// The badge first breathed by animating its own <c>Opacity</c>, which took the
/// session count inside it down to 45 % with the pill - the number spent half of
/// every cycle half gone, on a badge whose entire job is to be read. The pulse now
/// runs on <c>Background</c> between two opaque theme colours. Both halves of that
/// are asserted here, because both are invisible to the compiler: a keyframe whose
/// resource did not resolve, or an opacity that crept back onto the Border, would
/// load and lay out perfectly.
/// </remarks>
[Collection(HeadlessTests.Name)]
[SuppressMessage("Performance", "CA1801:Review unused parameters",
    Justification = "The fixture parameter forces xUnit to start the Avalonia session.")]
public class WorkingBadgeTests(HeadlessAppFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_pulse_fades_the_top_pill_and_nothing_else()
    {
        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        Theme theme = ThemeCatalog.BuiltIn.Single(t => t.Id == "claude");

        (double first, double later, double countOpacity) = HeadlessAppFixture.Invoke(() =>
        {
            ThemeApplier.Apply(Application.Current!, theme, string.Empty, osdTransparency: 0d);

            TaskbarWidgetWindow window = Shown(Working());
            Border lit = Pill(window, "lit");
            Border dim = Pill(window, "dim");

            // The two ends of the breath are the theme's, and they are the pair
            // Theme derives together - the whole point of not naming colours here.
            Fill(lit).Should().Be(ToColor(theme.Primary));
            Fill(dim).Should().Be(ToColor(theme.PrimaryPulse));

            double start = lit.Opacity;

            // Far enough into the 1.2 s cycle for the fade to have moved
            // appreciably - the easing is slow at both ends, so a couple of
            // frames would prove nothing either way.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(30);
            Dispatcher.UIThread.RunJobs();

            (double Start, double Later, double Count) reading =
                (start, lit.Opacity, Count(window).Opacity);
            window.Close();
            return reading;
        });

        // Not exactly 1: the first frame of the cycle has already been drawn by
        // the time anything can look, and the easing is under way.
        first.Should().BeGreaterThan(0.9d, "the badge starts on the primary");
        later.Should().BeLessThan(first, "the badge is supposed to be breathing");
        later.Should().BeGreaterThanOrEqualTo(0d);

        // The count is not inside the layer that fades, which is the entire
        // reason the badge is three layers instead of one.
        countOpacity.Should().Be(1d, "the number is the one thing on the badge that is read");
    }

    [Fact]
    public void The_count_sits_outside_the_fading_pill()
    {
        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        bool nested = HeadlessAppFixture.Invoke(() =>
        {
            ThemeApplier.Apply(
                Application.Current!,
                ThemeCatalog.BuiltIn[0],
                string.Empty,
                osdTransparency: 0d);

            TaskbarWidgetWindow window = Shown(Working());
            Border lit = Pill(window, "lit");

            // Opacity composites down the visual tree, so "the count keeps its
            // own opacity" is only worth anything if the count is not a child of
            // the thing being faded. That is the mistake this replaced.
            bool inside = lit.GetVisualDescendants().Contains(Count(window));
            window.Close();
            return inside;
        });

        nested.Should().BeFalse("a child of the animated pill would fade with it");
    }

    [Fact]
    public void The_badge_carries_the_session_count()
    {
        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        string text = HeadlessAppFixture.Invoke(() =>
        {
            ThemeApplier.Apply(
                Application.Current!,
                ThemeCatalog.BuiltIn[0],
                string.Empty,
                osdTransparency: 0d);

            TaskbarWidgetWindow window = Shown(Working());
            string reading = Count(window).Text ?? string.Empty;
            window.Close();
            return reading;
        });

        text.Should().Be("2/2", "two sessions are open and both are mid-turn");
    }

    [Fact]
    public void The_mark_gives_way_to_three_dots_while_a_turn_is_in_progress()
    {
        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        Theme theme = ThemeCatalog.BuiltIn.Single(t => t.Id == "claude");

        (bool markShown, bool dotsShown, int dots, Color ink, double first, double later) = HeadlessAppFixture.Invoke(() =>
        {
            ThemeApplier.Apply(Application.Current!, theme, string.Empty, osdTransparency: 0d);

            TaskbarWidgetWindow window = Shown(Working());
            Ellipse[] found = Dots(window);
            Color fill = found[0].Fill is ISolidColorBrush brush
                ? brush.Color
                : throw new InvalidOperationException("A working dot lost its fill.");

            double start = found[0].Opacity;

            // The animation clock runs on wall time, not on render ticks: under a
            // loaded test run twenty ticks can pass in under a millisecond and
            // the dot has not moved. A real 150 ms is a sixth of the cycle, where
            // the first dot - the one with no delay - is well on its way up.
            Thread.Sleep(150);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();

            (bool, bool, int, Color, double, double) reading =
                (Mark(window).IsVisible, DotRow(window).IsVisible, found.Length, fill, start, found[0].Opacity);
            window.Close();
            return reading;
        });

        markShown.Should().BeFalse("the dots take the mark's place, not a place beside it");
        dotsShown.Should().BeTrue();
        dots.Should().Be(3, "it is Claude's own three-dot thinking sign");
        ink.Should().Be(ToColor(theme.Primary), "the dots are drawn in the mark's ink");
        later.Should().NotBe(first, "the dots are supposed to be pulsing");
    }

    [Fact]
    public void The_mark_is_back_the_moment_nothing_is_working()
    {
        fixture.Should().NotBeNull("the fixture is what boots Avalonia for this collection");

        (bool markShown, bool dotsShown) = HeadlessAppFixture.Invoke(() =>
        {
            ThemeApplier.Apply(
                Application.Current!,
                ThemeCatalog.BuiltIn[0],
                string.Empty,
                osdTransparency: 0d);

            // One open session, waiting at its prompt: a badge reading 0/1, no dots.
            TaskbarWidgetWindow window = Shown(
                [new ClaudeSession("s1", "C:\\Proyectos\\one", Now.AddMinutes(-42), Now.AddMinutes(-3))]);
            (bool, bool) reading = (Mark(window).IsVisible, DotRow(window).IsVisible);
            window.Close();
            return reading;
        });

        markShown.Should().BeTrue("an open but idle session is not a reason to hide the mark");
        dotsShown.Should().BeFalse();
    }

    /// <summary>Two sessions mid-turn, which is what puts the count on the badge.</summary>
    private static IReadOnlyList<ClaudeSession> Working() =>
    [
        new ClaudeSession("s1", "C:\\Proyectos\\one", Now.AddMinutes(-42), Now.AddMinutes(-3), IsWorking: true),
        new ClaudeSession("s2", "C:\\Proyectos\\two", Now.AddMinutes(-12), Now.AddMinutes(-1), IsWorking: true),
    ];

    private static TaskbarWidgetWindow Shown(IReadOnlyList<ClaudeSession> sessions)
    {
        var viewModel = new TaskbarWidgetViewModel(TestLocalizer.English());
        viewModel.Configure(80d, showFable: false, followSystem: false);
        viewModel.UpdateSessions(sessions, Now);
        viewModel.ShowSessions = true;

        var window = new TaskbarWidgetWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>One of the badge's two stacked pills, by its class.</summary>
    private static Border Pill(Window window, string which) => window.GetVisualDescendants()
        .OfType<Border>()
        .Single(b => b.Classes.Contains("badgePill") && b.Classes.Contains(which));

    private static Avalonia.Controls.Shapes.Path Mark(Window window) => window.GetVisualDescendants()
        .OfType<Avalonia.Controls.Shapes.Path>()
        .Single(p => p.Classes.Contains("widgetMark"));

    private static StackPanel DotRow(Window window) => window.GetVisualDescendants()
        .OfType<StackPanel>()
        .Single(p => p.Classes.Contains("workingDots"));

    private static Ellipse[] Dots(Window window) => window.GetVisualDescendants()
        .OfType<Ellipse>()
        .Where(e => e.Classes.Contains("workingDot"))
        .ToArray();

    private static TextBlock Count(Window window) => window.GetVisualDescendants()
        .OfType<TextBlock>()
        .Single(t => t.Classes.Contains("sessionCount"));

    private static Color Fill(Border pill) => pill.Background is ISolidColorBrush brush
        ? brush.Color
        : throw new InvalidOperationException("A badge pill lost its background brush.");

    private static Color ToColor(Rgb colour) => Color.FromRgb(colour.R, colour.G, colour.B);
}
