using System.Net;
using System.Text.Json;
using ClaudeStatus.Export;
using ClaudeStatus.Platform;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// The contract the iCUE widget reads: what goes into the document, and how the
/// loopback server hands it out and turns web pages away.
/// </summary>
public class UsageExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(double session = 56, double week = 18, double? fable = 41, bool stale = false)
        => new(
            UsageWindow.Create(session, Now + new TimeSpan(2, 11, 0)),
            UsageWindow.Create(week, Now + TimeSpan.FromDays(3)),
            fable is null ? null : UsageWindow.Create(fable.Value, Now + TimeSpan.FromDays(3)),
            new Dictionary<string, UsageWindow>(),
            Now - TimeSpan.FromMinutes(4),
            stale);

    private static UsageExport Map(
        UsageSnapshot? snapshot,
        IndicatorAlert alert = IndicatorAlert.None,
        VelocityAlert? pace = null,
        UsageExportSessions? sessions = null)
        => UsageExportMapper.Map(snapshot, alert, pace, sessions, 80, Now, "1.2.3");

    // ---- the document ----

    [Fact]
    public void A_reading_maps_to_the_three_windows_with_their_spans()
    {
        UsageExport export = Map(Snapshot());

        export.Schema.Should().Be(UsageExport.CurrentSchema);
        export.State.Should().Be(UsageExportState.Ok);
        export.AppVersion.Should().Be("1.2.3");
        export.FetchedAt.Should().Be(Now - TimeSpan.FromMinutes(4));
        export.IsStale.Should().BeFalse();
        export.ThresholdPercent.Should().Be(80);
        export.Windows.Select(w => w.Id).Should().Equal("session", "week", "weekFable");
        export.Windows.Select(w => w.Percent).Should().Equal(56, 18, 41);
        export.Windows.Select(w => w.SpanSeconds).Should().Equal(5 * 3600, 7 * 86400, 7 * 86400);
        export.Windows[0].ResetsAt.Should().Be(Now + new TimeSpan(2, 11, 0));
        export.Pace.Should().BeNull();
        export.Sessions.Should().BeNull();
    }

    [Fact]
    public void A_plan_without_a_fable_window_omits_it()
        => Map(Snapshot(fable: null)).Windows.Select(w => w.Id).Should().Equal("session", "week");

    [Theory]
    [InlineData(IndicatorAlert.NeedsCredential, UsageExportState.NoCredential)]
    [InlineData(IndicatorAlert.Unreachable, UsageExportState.Unreachable)]
    [InlineData(IndicatorAlert.None, UsageExportState.Ok)]
    public void The_state_follows_the_indicator_alert(IndicatorAlert alert, UsageExportState expected)
        => Map(Snapshot(), alert).State.Should().Be(expected);

    [Fact]
    public void No_reading_and_no_alert_is_no_data_with_no_windows()
    {
        UsageExport export = Map(null);

        export.State.Should().Be(UsageExportState.NoData);
        export.Windows.Should().BeEmpty();
        export.FetchedAt.Should().BeNull();
    }

    [Fact]
    public void An_unreachable_endpoint_still_carries_the_last_reading()
    {
        UsageExport export = Map(Snapshot(stale: true), IndicatorAlert.Unreachable);

        export.State.Should().Be(UsageExportState.Unreachable);
        export.IsStale.Should().BeTrue();
        export.Windows.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(99.96, true)]
    [InlineData(99.9, false)]
    public void A_window_is_exhausted_where_the_indicators_say_so(double percent, bool exhausted)
        => Map(Snapshot(session: percent)).Windows[0].Exhausted.Should().Be(exhausted);

    [Fact]
    public void The_pace_alert_is_projected_to_absolute_times()
    {
        var alert = new VelocityAlert(VelocityWindow.Session, 30, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(131));

        UsageExportPace? pace = Map(Snapshot(), pace: alert).Pace;

        pace.Should().NotBeNull();
        pace!.WindowId.Should().Be("session");
        pace.PercentPerHour.Should().Be(30);
        pace.RunsOutAt.Should().Be(Now + TimeSpan.FromMinutes(40));
        pace.ResetsAt.Should().Be(Now + TimeSpan.FromMinutes(131));
    }

    [Fact]
    public void The_week_pace_names_the_week_window()
        => Map(Snapshot(), pace: new VelocityAlert(VelocityWindow.Week, 2, TimeSpan.FromHours(20), TimeSpan.FromDays(3)))
            .Pace!.WindowId.Should().Be("week");

    [Fact]
    public void The_session_count_passes_through()
        => Map(Snapshot(), sessions: new UsageExportSessions(1, 3)).Sessions.Should().Be(new UsageExportSessions(1, 3));

    // ---- the settings ----

    [Fact]
    public void The_export_is_off_by_default()
    {
        AppSettings settings = new AppSettings().Normalized();

        settings.EnableUsageExport.Should().BeFalse();
        settings.UsageExportPort.Should().Be(AppSettings.DefaultUsageExportPort);
    }

    [Theory]
    [InlineData(null, AppSettings.DefaultUsageExportPort)]
    [InlineData(0, AppSettings.DefaultUsageExportPort)]
    [InlineData(80, AppSettings.DefaultUsageExportPort)]
    [InlineData(70000, AppSettings.DefaultUsageExportPort)]
    [InlineData(5000, 5000)]
    [InlineData(65535, 65535)]
    public void A_port_outside_the_registered_range_falls_back_to_the_default(int? stored, int expected)
        => new AppSettings { UsageExportPort = stored }.Normalized().UsageExportPort.Should().Be(expected);

    // ---- the server ----

    /// <summary>A server on a port the OS picked, with a client pointed at it.</summary>
    private sealed class Running : IDisposable
    {
        public UsageExportServer Server { get; } = new(0);
        public HttpClient Client { get; }

        public Running()
        {
            Server.Start();
            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Server.Port}/"), Timeout = TimeSpan.FromSeconds(10) };
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    [Fact]
    public async Task The_latest_document_is_served_as_json_with_cors()
    {
        using var running = new Running();
        running.Server.Latest = Map(Snapshot(), sessions: new UsageExportSessions(1, 3));

        using HttpResponseMessage response = await running.Client.GetAsync("v1/usage", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal("*");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("schema").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("state").GetString().Should().Be("Ok");
        body.RootElement.GetProperty("windows").GetArrayLength().Should().Be(3);
        body.RootElement.GetProperty("windows")[0].GetProperty("id").GetString().Should().Be("session");
        body.RootElement.GetProperty("sessions").GetProperty("working").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Before_the_first_document_the_usage_path_answers_503()
    {
        using var running = new Running();

        using HttpResponseMessage response = await running.Client.GetAsync("v1/usage", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Health_answers_at_once()
    {
        using var running = new Running();

        using HttpResponseMessage response = await running.Client.GetAsync("v1/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task An_unknown_path_is_404_and_a_wrong_method_is_405()
    {
        using var running = new Running();

        using HttpResponseMessage missing = await running.Client.GetAsync("v1/nothing", TestContext.Current.CancellationToken);
        using HttpResponseMessage wrongMethod = await running.Client.PostAsync("v1/usage", null, TestContext.Current.CancellationToken);

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        wrongMethod.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task A_post_to_open_raises_the_event()
    {
        using var running = new Running();
        int raised = 0;
        running.Server.OpenRequested += (_, _) => Interlocked.Increment(ref raised);

        using HttpResponseMessage response = await running.Client.PostAsync("v1/open", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        raised.Should().Be(1);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:3000")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public async Task A_web_page_is_refused_without_cors_whatever_it_asks_for(string origin)
    {
        using var running = new Running();
        running.Server.Latest = Map(Snapshot());
        using var usage = new HttpRequestMessage(HttpMethod.Get, "v1/usage");
        usage.Headers.Add("Origin", origin);
        using var open = new HttpRequestMessage(HttpMethod.Post, "v1/open");
        open.Headers.Add("Origin", origin);
        int raised = 0;
        running.Server.OpenRequested += (_, _) => Interlocked.Increment(ref raised);

        using HttpResponseMessage usageResponse = await running.Client.SendAsync(usage, TestContext.Current.CancellationToken);
        using HttpResponseMessage openResponse = await running.Client.SendAsync(open, TestContext.Current.CancellationToken);

        usageResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        usageResponse.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        (await usageResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        openResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        raised.Should().Be(0);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("file://")]
    [InlineData("qrc://widget")]
    public async Task An_embedded_or_local_document_is_served_with_its_origin_echoed(string origin)
    {
        using var running = new Running();
        running.Server.Latest = Map(Snapshot());
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/usage");
        request.Headers.Add("Origin", origin);

        using HttpResponseMessage response = await running.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Equal(origin);
        response.Headers.Vary.Should().Contain("Origin");
    }

    [Fact]
    public async Task A_preflight_is_answered_with_the_allowed_methods()
    {
        using var running = new Running();
        using var request = new HttpRequestMessage(HttpMethod.Options, "v1/open");
        request.Headers.Add("Origin", "null");

        using HttpResponseMessage response = await running.Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Methods").Single().Should().Contain("POST");
    }

    [Fact]
    public async Task Stopping_releases_the_port()
    {
        using var running = new Running();
        int port = running.Server.Port;
        running.Server.Stop();

        running.Server.IsListening.Should().BeFalse();
        Func<Task> connect = () => running.Client.GetAsync("v1/health", TestContext.Current.CancellationToken);
        await connect.Should().ThrowAsync<HttpRequestException>();

        // And another server can take the port straight away.
        using var next = new UsageExportServer(port);
        next.Start();
        next.Port.Should().Be(port);
    }

    // ---- the socket ----

    private static async Task<string> ReceiveTextAsync(System.Net.WebSockets.ClientWebSocket socket)
    {
        byte[] buffer = new byte[64 * 1024];
        System.Net.WebSockets.WebSocketReceiveResult result =
            await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
        result.MessageType.Should().Be(System.Net.WebSockets.WebSocketMessageType.Text);
        result.EndOfMessage.Should().BeTrue();
        return System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Fact]
    public async Task A_subscriber_gets_the_latest_document_at_once_and_every_change_after()
    {
        using var running = new Running();
        running.Server.Latest = Map(Snapshot(session: 10));
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "null");

        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{running.Server.Port}/v1/usage"), TestContext.Current.CancellationToken);
        string first = await ReceiveTextAsync(socket);
        running.Server.Latest = Map(Snapshot(session: 20));
        string second = await ReceiveTextAsync(socket);

        running.Server.SubscriberCount.Should().Be(1);
        using JsonDocument a = JsonDocument.Parse(first);
        using JsonDocument b = JsonDocument.Parse(second);
        a.RootElement.GetProperty("windows")[0].GetProperty("percent").GetDouble().Should().Be(10);
        b.RootElement.GetProperty("windows")[0].GetProperty("percent").GetDouble().Should().Be(20);

        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_subscriber_saying_open_raises_the_event()
    {
        using var running = new Running();
        int raised = 0;
        var signalled = new TaskCompletionSource();
        running.Server.OpenRequested += (_, _) => { Interlocked.Increment(ref raised); signalled.TrySetResult(); };
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{running.Server.Port}/v1/usage"), TestContext.Current.CancellationToken);

        await socket.SendAsync("open"u8.ToArray(), System.Net.WebSockets.WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        await socket.SendAsync("something else"u8.ToArray(), System.Net.WebSockets.WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        raised.Should().Be(1);
    }

    [Fact]
    public async Task A_subscriber_may_write_a_log_line_and_stays_connected()
    {
        using var running = new Running();
        int raised = 0;
        running.Server.OpenRequested += (_, _) => Interlocked.Increment(ref raised);
        running.Server.Latest = Map(Snapshot());
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{running.Server.Port}/v1/usage"), TestContext.Current.CancellationToken);
        await ReceiveTextAsync(socket);

        await socket.SendAsync("log:style {\"used\":\"default\"}"u8.ToArray(), System.Net.WebSockets.WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        await socket.SendAsync(new byte[UsageExportServer.MaxMessageBytes * 3], System.Net.WebSockets.WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        running.Server.Latest = Map(Snapshot(session: 33));
        string next = await ReceiveTextAsync(socket);

        raised.Should().Be(0);
        next.Should().Contain("33");
        running.Server.SubscriberCount.Should().Be(1);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two\nlines", "two lines")]
    [InlineData("tab\there\r\n[INF] forged", "tab here  [INF] forged")]
    public void A_log_line_cannot_carry_control_characters(string text, string expected)
        => UsageExportServer.Printable(text).Should().Be(expected);

    [Fact]
    public async Task A_web_page_cannot_subscribe()
    {
        using var running = new Running();
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://example.com");

        Func<Task> connect = () => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{running.Server.Port}/v1/usage"), TestContext.Current.CancellationToken);

        await connect.Should().ThrowAsync<System.Net.WebSockets.WebSocketException>();
        running.Server.SubscriberCount.Should().Be(0);
    }

    [Fact]
    public async Task An_upgrade_on_another_path_is_not_a_socket()
    {
        using var running = new Running();
        using var socket = new System.Net.WebSockets.ClientWebSocket();

        Func<Task> connect = () => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{running.Server.Port}/v1/health"), TestContext.Current.CancellationToken);

        await connect.Should().ThrowAsync<System.Net.WebSockets.WebSocketException>();
    }

    [Fact]
    public async Task Stopping_drops_the_subscribers()
    {
        using var running = new Running();
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{running.Server.Port}/v1/usage"), TestContext.Current.CancellationToken);
        running.Server.SubscriberCount.Should().Be(1);

        running.Server.Stop();

        running.Server.SubscriberCount.Should().Be(0);
        byte[] buffer = new byte[16];
        Func<Task> receive = () => socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);
        await receive.Should().ThrowAsync<System.Net.WebSockets.WebSocketException>();
    }

    [Fact]
    public void The_server_binds_loopback_only()
    {
        using var server = new UsageExportServer(0);
        server.Start();

        server.Port.Should().BeInRange(1, ushort.MaxValue);
        server.IsListening.Should().BeTrue();
        UsageExportServer.IsWebOrigin(null).Should().BeFalse();
        UsageExportServer.IsWebOrigin("https://x").Should().BeTrue();
    }
}
