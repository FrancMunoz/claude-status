#if WINDOWS10_0_17763_0_OR_GREATER
using System.Xml.Linq;
using ClaudeStatus.Platform.Windows;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The Windows toast notifier's pure parts, and its refusal to post for an app id
/// nobody installed.
/// </summary>
/// <remarks>
/// Nothing here shows a toast: whether one appears depends on whether this
/// machine has the app installed, and a test that pops notifications at whoever
/// runs it is a test people learn to skip.
/// </remarks>
public sealed class WindowsToastNotifierTests
{
    [Fact]
    public void The_toast_carries_title_and_message_as_text_not_markup()
    {
        string xml = WindowsToastNotifier.BuildXml("<b>title</b>", "a & b </text><text>injected", "tag");

        XElement[] texts = [.. XDocument.Parse(xml).Descendants("text")];

        texts.Select(t => t.Value).Should().Equal("<b>title</b>", "a & b </text><text>injected");
    }

    [Theory]
    [InlineData("3f2c9a1e-5b7d-4c1a-9e2f-0a1b2c3d4e5f")]
    [InlineData("a tag with \"quotes\" & <angles>")]
    public void A_click_reports_the_tag_the_toast_was_shown_with(string tag)
    {
        string launch = XDocument.Parse(WindowsToastNotifier.BuildXml("t", "m", tag)).Root!.Attribute("launch")!.Value;

        WindowsToastNotifier.TagFromArguments(launch).Should().Be(tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-else's arguments")]
    public void A_toast_without_a_tag_of_ours_reports_none(string? arguments)
    {
        string? fromOwn = WindowsToastNotifier.TagFromArguments(
            XDocument.Parse(WindowsToastNotifier.BuildXml("t", "m", null)).Root!.Attribute("launch")!.Value);

        fromOwn.Should().BeNull();
        WindowsToastNotifier.TagFromArguments(arguments).Should().BeNull();
    }

    [Fact]
    public void An_app_id_nobody_installed_is_not_supported_and_shows_nothing()
    {
        using var notifier = new WindowsToastNotifier("ClaudeStatus.Tests.NotInstalled." + Guid.NewGuid().ToString("N"));

        notifier.IsSupported.Should().BeFalse();
        notifier.Notify("title", "message", "tag").Should().BeFalse();
    }

    [Fact]
    public void The_shell_icon_fallback_is_usable_before_it_has_added_any_icon()
    {
        using var notifier = new ShellNotifyIconNotifier();

        notifier.IsSupported.Should().BeTrue();
    }
}
#endif
