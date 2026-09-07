using System.Text.Json;
using ClaudeStatus.App.ViewModels;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Checks the release configuration against the app that consumes it.
/// </summary>
/// <remarks>
/// <para>
/// The release pipeline and the updater are wired together by nothing but two
/// strings in two different files, in two different languages, edited months
/// apart. If they disagree, releases go to one repository while every installed
/// copy checks another - and the only symptom is that updates never arrive, which
/// is indistinguishable from there being no new version.
/// </para>
/// <para>
/// Nothing here can be checked by running the app, because the mistake only shows
/// up in the field on somebody else's machine.
/// </para>
/// </remarks>
public class ReleaseConfigTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClaudeStatus.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.SkipWhen(directory is null, "Running outside the repository; source files unavailable.");
        return directory!.FullName;
    }

    private static JsonElement ReleaseConfig()
    {
        string path = Path.Combine(RepositoryRoot(), ".releaserc.json");
        Assert.SkipUnless(File.Exists(path), ".releaserc.json is not present.");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void Releases_are_published_where_the_updater_looks_for_them()
    {
        string configured = ReleaseConfig().GetProperty("repositoryUrl").GetString()!;

        configured.Should().Be(
            InfoViewModel.ProjectUrl,
            "semantic-release publishes here and VelopackUpdateService reads from there");
    }

    [Fact]
    public void The_update_feed_files_are_attached_to_the_release()
    {
        // Velopack's GithubSource reads releases.win.json to find out what exists,
        // and the .nupkg is the payload it downloads. An installer attached without
        // them is one people can install and then never update.
        // A plugin entry is either a bare name or a [name, options] pair; only the
        // pairs can carry assets.
        JsonElement github = ReleaseConfig()
            .GetProperty("plugins")
            .EnumerateArray()
            .Single(plugin =>
                plugin.ValueKind == JsonValueKind.Array
                && plugin[0].GetString() == "@semantic-release/github");

        string[] assets = github[1]
            .GetProperty("assets")
            .EnumerateArray()
            .Select(asset => asset.GetProperty("path").GetString()!)
            .ToArray();

        assets.Should().Contain(path => path.EndsWith("releases.win.json", StringComparison.Ordinal));
        assets.Should().Contain(path => path.EndsWith(".nupkg", StringComparison.Ordinal));
        assets.Should().Contain(path => path.EndsWith("Setup.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void The_tag_format_is_the_one_MinVer_is_configured_to_read()
    {
        // Directory.Build.props sets MinVerTagPrefix to "v". A tagFormat without
        // it would leave every build reporting 0.0.0-alpha.0 forever, because
        // MinVer would not recognise any of the tags as versions.
        string format = ReleaseConfig().GetProperty("tagFormat").GetString()!;

        format.Should().Be("v${version}");

        string props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        props.Should().Contain(
            "<MinVerTagPrefix>v</MinVerTagPrefix>",
            "the tag semantic-release writes has to be the one MinVer reads");
    }
}
