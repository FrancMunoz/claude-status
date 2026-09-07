using ClaudeStatus.Config;

namespace ClaudeStatus.Core.Tests;

/// <summary>The two settings that choose and shape the indicator.</summary>
public class IndicatorSettingsTests
{
    [Fact]
    public void The_widget_is_the_default_and_fable_is_hidden_by_default()
    {
        var settings = new AppSettings();

        settings.Indicator.Should().Be(IndicatorKind.TaskbarWidget);
        settings.ShowFableInWidget.Should().BeFalse(
            "most plans hit the session or weekly limit long before the Fable one");
        settings.WidgetFollowsSystem.Should().BeTrue(
            "a taskbar widget should look like part of the taskbar; the card is opt-in");
    }

    [Fact]
    public void An_unknown_indicator_kind_normalises_to_the_widget()
    {
        // A settings file edited by hand, or written by a future version.
        var settings = new AppSettings { Indicator = (IndicatorKind)42 };

        settings.Normalized().Indicator.Should().Be(IndicatorKind.TaskbarWidget);
    }

    [Fact]
    public void A_valid_choice_survives_normalisation()
    {
        var settings = new AppSettings { Indicator = IndicatorKind.TrayIcon, ShowFableInWidget = true };

        AppSettings normalised = settings.Normalized();

        normalised.Indicator.Should().Be(IndicatorKind.TrayIcon);
        normalised.ShowFableInWidget.Should().BeTrue();
    }
}
