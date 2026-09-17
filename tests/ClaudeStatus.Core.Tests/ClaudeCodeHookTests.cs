using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeStatus.Sessions;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Writing our hooks into Claude Code's settings file.
/// </summary>
/// <remarks>
/// This edits another application's configuration - one it needs in order to
/// start. The tests that matter here are the ones proving we leave everything
/// else exactly as we found it, and that a file we cannot understand is not
/// written to at all.
/// </remarks>
public class ClaudeCodeHookTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-hooks-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Settings => Path.Combine(_directory, "settings.json");

    private ClaudeCodeHookManager Manager(string exe = "C:\\Apps\\ClaudeStatus.exe")
        => new(() => exe, () => Settings);

    private void Write(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Settings, json);
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(Settings))!;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Installing_adds_one_group_per_event()
    {
        Write("{}");

        HookSyncResult result = await Manager().SyncAsync(enabled: true, Ct);

        result.Outcome.Should().Be(HookSyncOutcome.Installed);

        JsonObject hooks = (JsonObject)Read()["hooks"]!;
        hooks.Select(pair => pair.Key).Should().BeEquivalentTo(ClaudeCodeHooks.Events);
    }

    [Fact]
    public async Task The_hook_uses_the_exec_form_so_no_shell_window_appears()
    {
        Write("{}");
        await Manager("C:\\Apps\\ClaudeStatus.exe").SyncAsync(enabled: true, Ct);

        JsonObject hook = (JsonObject)((JsonArray)((JsonObject)
            ((JsonArray)((JsonObject)Read()["hooks"]!)["Stop"]!)[0]!)["hooks"]!)[0]!;

        hook["command"]!.GetValue<string>().Should().Be("C:\\Apps\\ClaudeStatus.exe");
        hook["args"]!.AsArray().Select(a => a!.GetValue<string>())
            .Should().Equal(ClaudeCodeHooks.Marker, "Stop");
        hook["async"]!.GetValue<bool>().Should().BeTrue("the turn must not wait on a notification");
    }

    [Fact]
    public async Task The_session_end_hook_is_synchronous_so_it_survives_the_shutdown()
    {
        // Fire-and-forget at exit never lands: the process is spawned moments
        // before its parent tears down and never gets to write. Measured - every
        // real session reported its turns and none ever reported ending.
        Write("{}");
        await Manager().SyncAsync(enabled: true, Ct);

        JsonObject end = (JsonObject)((JsonArray)((JsonObject)
            ((JsonArray)((JsonObject)Read()["hooks"]!)["SessionEnd"]!)[0]!)["hooks"]!)[0]!;
        JsonObject stop = (JsonObject)((JsonArray)((JsonObject)
            ((JsonArray)((JsonObject)Read()["hooks"]!)["Stop"]!)[0]!)["hooks"]!)[0]!;

        end["async"]!.GetValue<bool>().Should().BeFalse("it has to finish before Claude Code exits");
        stop["async"]!.GetValue<bool>().Should().BeTrue("a turn must never wait on us");
    }

    [Fact]
    public async Task Somebody_elses_hooks_are_left_exactly_alone()
    {
        Write("""
        {
          "hooks": {
            "Stop": [
              { "hooks": [ { "type": "command", "command": "my-own-thing.sh" } ] }
            ],
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "log-it.sh" } ] }
            ]
          }
        }
        """);

        await Manager().SyncAsync(enabled: true, Ct);

        JsonObject hooks = (JsonObject)Read()["hooks"]!;

        // Their Stop hook is still first, with ours added after it.
        JsonArray stop = (JsonArray)hooks["Stop"]!;
        stop.Should().HaveCount(2);
        ((JsonObject)((JsonArray)((JsonObject)stop[0]!)["hooks"]!)[0]!)["command"]!
            .GetValue<string>().Should().Be("my-own-thing.sh");

        // And an event we never touch is untouched.
        JsonArray pre = (JsonArray)hooks["PreToolUse"]!;
        pre.Should().ContainSingle();
        ((JsonObject)pre[0]!)["matcher"]!.GetValue<string>().Should().Be("Bash");
    }

    [Fact]
    public async Task Every_other_setting_survives_a_round_trip()
    {
        // Including keys this version has never heard of - the reason the file is
        // edited as a DOM rather than deserialized into a model of our own.
        Write("""
        {
          "model": "opus[1m]",
          "statusLine": { "type": "command", "command": "statusline.ps1" },
          "somethingFromNextYear": { "nested": [1, 2, 3] }
        }
        """);

        await Manager().SyncAsync(enabled: true, Ct);

        JsonObject after = Read();
        after["model"]!.GetValue<string>().Should().Be("opus[1m]");
        after["statusLine"]!["command"]!.GetValue<string>().Should().Be("statusline.ps1");
        after["somethingFromNextYear"]!["nested"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public async Task Installing_twice_does_not_add_a_second_copy()
    {
        Write("{}");
        await Manager().SyncAsync(enabled: true, Ct);

        HookSyncResult second = await Manager().SyncAsync(enabled: true, Ct);

        second.Outcome.Should().Be(HookSyncOutcome.Unchanged, "nothing changed, so nothing is written");
        ((JsonArray)((JsonObject)Read()["hooks"]!)["Stop"]!).Should().ContainSingle();
    }

    [Fact]
    public async Task A_moved_executable_is_repointed_rather_than_duplicated()
    {
        // What happens on every update: same hooks, new path. Matching on the
        // marker instead of the path is what makes this work.
        Write("{}");
        await Manager("C:\\Apps\\v1\\ClaudeStatus.exe").SyncAsync(enabled: true, Ct);

        await Manager("C:\\Apps\\v2\\ClaudeStatus.exe").SyncAsync(enabled: true, Ct);

        JsonArray stop = (JsonArray)((JsonObject)Read()["hooks"]!)["Stop"]!;
        stop.Should().ContainSingle("the old entry is replaced, not joined");
        ((JsonObject)((JsonArray)((JsonObject)stop[0]!)["hooks"]!)[0]!)["command"]!
            .GetValue<string>().Should().Be("C:\\Apps\\v2\\ClaudeStatus.exe");
    }

    [Fact]
    public async Task Removing_takes_ours_out_and_leaves_theirs()
    {
        Write("""
        {
          "model": "opus",
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "my-own-thing.sh" } ] } ]
          }
        }
        """);
        await Manager().SyncAsync(enabled: true, Ct);

        HookSyncResult result = await Manager().SyncAsync(enabled: false, Ct);

        result.Outcome.Should().Be(HookSyncOutcome.Removed);

        JsonObject after = Read();
        after["model"]!.GetValue<string>().Should().Be("opus");

        JsonArray stop = (JsonArray)((JsonObject)after["hooks"]!)["Stop"]!;
        stop.Should().ContainSingle();
        ((JsonObject)((JsonArray)((JsonObject)stop[0]!)["hooks"]!)[0]!)["command"]!
            .GetValue<string>().Should().Be("my-own-thing.sh");
    }

    [Fact]
    public async Task Removing_leaves_no_empty_containers_behind()
    {
        Write("{\"model\":\"opus\"}");
        await Manager().SyncAsync(enabled: true, Ct);

        await Manager().SyncAsync(enabled: false, Ct);

        Read().ContainsKey("hooks").Should().BeFalse("an empty hooks object is litter we put there");
    }

    [Fact]
    public async Task Removing_from_a_file_that_never_had_hooks_writes_nothing()
    {
        Write("{\"model\":\"opus\"}");
        DateTime before = File.GetLastWriteTimeUtc(Settings);

        HookSyncResult result = await Manager().SyncAsync(enabled: false, Ct);

        result.Outcome.Should().Be(HookSyncOutcome.Unchanged);
        File.GetLastWriteTimeUtc(Settings).Should().Be(before, "the file was not touched at all");
    }

    [Fact]
    public async Task A_settings_file_we_cannot_parse_is_never_overwritten()
    {
        // Half-edited by hand, or written by a newer Claude Code. Replacing it
        // with our idea of its contents is the worst thing we could do.
        const string broken = "{ \"model\": \"opus\", oops";
        Write(broken);

        HookSyncResult result = await Manager().SyncAsync(enabled: true, Ct);

        result.Outcome.Should().Be(HookSyncOutcome.Failed);
        result.MessageKey.Should().Be("Hooks_Error_Unreadable");
        File.ReadAllText(Settings).Should().Be(broken, "left exactly as found");
    }

    [Fact]
    public async Task A_settings_file_that_is_not_an_object_is_never_overwritten()
    {
        Write("[1, 2, 3]");

        HookSyncResult result = await Manager().SyncAsync(enabled: true, Ct);

        result.Outcome.Should().Be(HookSyncOutcome.Failed);
        File.ReadAllText(Settings).Should().Be("[1, 2, 3]");
    }

    [Fact]
    public async Task A_missing_settings_file_is_created_when_installing()
    {
        Directory.CreateDirectory(_directory);

        (await Manager().SyncAsync(enabled: true, Ct)).Outcome.Should().Be(HookSyncOutcome.Installed);

        ClaudeCodeHooks.IsInstalled(Read()).Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_settings_file_is_not_created_just_to_remove_nothing()
    {
        Directory.CreateDirectory(_directory);

        (await Manager().SyncAsync(enabled: false, Ct)).Outcome.Should().Be(HookSyncOutcome.Unchanged);

        File.Exists(Settings).Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_settings_file_is_treated_as_an_empty_object()
    {
        Write("   ");

        (await Manager().SyncAsync(enabled: true, Ct)).Outcome.Should().Be(HookSyncOutcome.Installed);
    }

    [Fact]
    public async Task IsInstalled_reports_what_is_actually_in_the_file()
    {
        Write("{}");
        ClaudeCodeHookManager manager = Manager();

        (await manager.IsInstalledAsync(Ct)).Should().BeFalse();
        await manager.SyncAsync(enabled: true, Ct);
        (await manager.IsInstalledAsync(Ct)).Should().BeTrue();
        await manager.SyncAsync(enabled: false, Ct);
        (await manager.IsInstalledAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public void A_hook_left_by_an_older_version_on_an_event_we_no_longer_use_is_cleaned_up()
    {
        // The removal pass walks every event in the file, not only the ones we
        // install today, or a renamed event would strand its entry forever.
        var settings = (JsonObject)JsonNode.Parse($$"""
        {
          "hooks": {
            "SomeRetiredEvent": [
              { "hooks": [ { "type": "command", "command": "old.exe", "args": ["{{ClaudeCodeHooks.Marker}}", "x"] } ] }
            ]
          }
        }
        """)!;

        ClaudeCodeHooks.Reconcile(settings, enabled: false, "C:\\new.exe").Should().BeTrue();

        settings.ContainsKey("hooks").Should().BeFalse();
    }

    [Fact]
    public void Every_installed_event_maps_back_to_something_we_understand()
    {
        // The two lists are written in different places and must not drift: a
        // hook we install whose event we cannot interpret would fire into nothing.
        ClaudeCodeHooks.Events.Should().OnlyContain(e => ClaudeCodeHooks.KindFor(e) != null);
    }

    [Fact]
    public void An_unknown_event_maps_to_nothing_rather_than_guessing()
    {
        ClaudeCodeHooks.KindFor("PreToolUse").Should().BeNull();
        ClaudeCodeHooks.KindFor(null).Should().BeNull();
    }

    [Fact]
    public async Task The_written_file_is_valid_indented_json()
    {
        Write("{\"model\":\"opus\"}");
        await Manager().SyncAsync(enabled: true, Ct);

        string text = File.ReadAllText(Settings);

        text.Should().Contain("\n  ", "a settings file a human edits should stay readable");
        Action parse = () => JsonDocument.Parse(text);
        parse.Should().NotThrow();
    }

    [Fact]
    public async Task No_temporary_files_are_left_behind()
    {
        Write("{}");
        await Manager().SyncAsync(enabled: true, Ct);

        Directory.GetFiles(_directory).Should().ContainSingle().Which.Should().Be(Settings);
    }
}
