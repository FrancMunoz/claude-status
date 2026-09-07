using System.Text.Json;
using System.Text.RegularExpressions;
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
    public void Semantic_release_publishes_from_the_branch_the_workflow_runs_on()
    {
        // These were master and main for a while, and the effect was total: the
        // workflow fired on every push, semantic-release read its own config,
        // decided master was not a release branch, and exited zero having done
        // nothing. A green tick on a release that never happened.
        string[] configured = ReleaseConfig()
            .GetProperty("branches")
            .EnumerateArray()
            .Select(branch => branch.GetString()!)
            .ToArray();

        string workflow = Path.Combine(
            RepositoryRoot(), ".github", "workflows", "release.yml");
        Assert.SkipUnless(File.Exists(workflow), "release.yml is not present.");

        // The trigger, as "branches: [name]" under the push event.
        Match trigger = Regex.Match(
            File.ReadAllText(workflow),
            @"branches:\s*\[\s*(?<name>[A-Za-z0-9._/-]+)\s*\]");

        trigger.Success.Should().BeTrue("release.yml must declare the branch it runs on");

        configured.Should().Contain(
            trigger.Groups["name"].Value,
            "semantic-release only releases from a branch it is configured for");
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
    public void The_macOS_release_carries_its_own_feed_as_well_as_its_installer()
    {
        // Velopack keeps a feed per platform: a Mac reads releases.osx.json and
        // never looks at the Windows one. Attaching the .pkg without it produces
        // exactly the failure the Windows assets were listed to avoid - an app
        // people can install and then never update - and it is a silent one,
        // because nothing about a missing feed looks like an error.
        string[] assets = GitHubAssets();

        assets.Should().Contain(path => path.EndsWith("releases.osx.json", StringComparison.Ordinal));
        assets.Should().Contain(path => path.EndsWith("Setup.pkg", StringComparison.Ordinal));
    }

    /// <summary>The asset paths the GitHub plugin is configured to upload.</summary>
    private static string[] GitHubAssets()
    {
        JsonElement github = ReleaseConfig()
            .GetProperty("plugins")
            .EnumerateArray()
            .Single(plugin =>
                plugin.ValueKind == JsonValueKind.Array
                && plugin[0].GetString() == "@semantic-release/github");

        return github[1]
            .GetProperty("assets")
            .EnumerateArray()
            .Select(asset => asset.GetProperty("path").GetString()!)
            .ToArray();
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
