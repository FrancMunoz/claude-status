using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeStatus.Config;
using Microsoft.Extensions.Logging;

namespace ClaudeStatus.Export;

/// <summary>
/// Serves the latest <see cref="UsageExport"/> on the loopback interface, for
/// the iCUE widget and anything else on this machine that wants the reading:
/// pushed over a WebSocket, and answered over plain HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a WebSocket.</b> The widget runs in iCUE's embedded browser, and
/// iCUE stands between that browser and the network: a widget's HTTP requests
/// go through its URL-permission layer, and as of iCUE 5.51 that layer lets
/// nothing through to a loopback address - not <c>fetch</c>, not XHR, not a
/// script or image tag, with or without the permission declared and approved.
/// Measured 2026-09-26: the only requests that ever reached this server from
/// the device were WebSocket handshakes. So the widget subscribes over
/// <c>ws://localhost:&lt;port&gt;/v1/usage</c>, receives the document at once
/// and again whenever it changes, and sends <c>open</c> to have the details
/// window shown. Which is the better design anyway: the app knows when the
/// reading changed, so nothing polls.
/// </para>
/// <para>
/// <b>Why a hand-written HTTP server.</b> The handshake is HTTP, and a browser
/// on the desk (where the widget is developed) uses plain <c>fetch</c>.
/// <c>HttpListener</c> sits on <c>http.sys</c> on Windows, whose URL
/// reservations trip non-elevated users; Kestrel is a framework for a service
/// that answers two GETs. Speaking just enough HTTP/1.1 over a
/// <see cref="TcpListener"/> is a hundred lines, works identically on every OS,
/// and has no configuration to go wrong. The WebSocket framing itself is the
/// runtime's (<see cref="WebSocket.CreateFromStream"/>); only the handshake is ours.
/// </para>
/// <para>
/// <b>What it refuses.</b> It binds to the loopback addresses only, so nothing
/// off the machine can connect. A browser on the machine still can: any web
/// page could <c>fetch("http://localhost:47831/…")</c> or open the socket. So a
/// request that carries a web <c>Origin</c> (<c>http://</c> or <c>https://</c>)
/// is answered 403 without the CORS header, and the page's script never sees
/// the body. iCUE's widgets send <c>Origin: null</c> - they are loaded from
/// disk - and so does a widget opened from disk for development. The document
/// holds no secret either way (see <see cref="UsageExport"/>); this keeps the
/// details window from being a thing a web page can pop open.
/// </para>
/// <para>
/// The request is read to the end of its headers and no further, capped at
/// <see cref="MaxRequestBytes"/> and <see cref="RequestTimeout"/>; the body of a
/// POST is never read. Every HTTP response closes the connection. Socket
/// subscribers may send <c>open</c> and nothing else; anything longer than a
/// short command is ignored.
/// </para>
/// <para>
/// Off by default (<see cref="AppSettings.EnableUsageExport"/>). The controller
/// starts it when the setting is on and hands it a fresh document after every
/// poll and session change; it never asks the monitor anything itself.
/// </para>
/// </remarks>
public sealed class UsageExportServer : IDisposable
{
    /// <summary>The path a consumer polls, or subscribes to over a WebSocket, for the document.</summary>
    public const string UsagePath = "/v1/usage";

    /// <summary>A path that answers as soon as the server is up, for a consumer to probe.</summary>
    public const string HealthPath = "/v1/health";

    /// <summary>The path a consumer POSTs to open the details window, as a tray click would.</summary>
    public const string OpenPath = "/v1/open";

    /// <summary>The text a socket subscriber sends to open the details window.</summary>
    public const string OpenCommand = "open";

    /// <summary>What a subscriber puts in front of a line it wants written to the app's log.</summary>
    public const string LogPrefix = "log:";

    /// <summary>The longest message a subscriber may send and have looked at.</summary>
    public const int MaxMessageBytes = 1024;

    /// <summary>How many log lines one connection may write, so a looping widget cannot fill the log.</summary>
    public const int MaxDiagnosticsPerSubscriber = 40;

    /// <summary>The most a request may be, headers included. Ours are a line and a handful of headers.</summary>
    public const int MaxRequestBytes = 8 * 1024;

    /// <summary>How long a connection may take to deliver its request.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often an idle socket is pinged, so a dead subscriber is noticed and dropped.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly byte[] HealthBody = "{\"ok\":true}"u8.ToArray();

    private readonly int _requestedPort;
    private readonly ILogger<UsageExportServer>? _log;

    /// <summary>One per loopback address family, IPv4 first. Empty while stopped.</summary>
    private readonly List<TcpListener> _listeners = [];
    private CancellationTokenSource? _stopping;
    private readonly List<Task> _accepting = [];
    private readonly List<Subscriber> _subscribers = [];
    private UsageExport? _latest;
    private bool _disposed;

    /// <summary>Web origins already logged as refused, so a page polling every few seconds writes one warning.</summary>
    private readonly HashSet<string> _refusedOrigins = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="port">The loopback port to listen on. 0 lets the OS pick one; see <see cref="Port"/>.</param>
    /// <param name="log">Where to say the server started or could not.</param>
    public UsageExportServer(int port, ILogger<UsageExportServer>? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        _requestedPort = port;
        _log = log;
    }

    /// <summary>The port actually bound, once <see cref="Start"/> has run. 0 before.</summary>
    public int Port { get; private set; }

    /// <summary>Whether the listener is up.</summary>
    public bool IsListening => _listeners.Count > 0 && _stopping is { IsCancellationRequested: false };

    /// <summary>How many socket subscribers are connected right now.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_subscribers)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>
    /// The document served at <see cref="UsagePath"/>. Null answers 503 until the
    /// first is set. Setting it pushes it to every socket subscriber.
    /// </summary>
    public UsageExport? Latest
    {
        get => Volatile.Read(ref _latest);
        set
        {
            Volatile.Write(ref _latest, value);
            if (value is not null)
            {
                Broadcast(Serialize(value));
            }
        }
    }

    /// <summary>A consumer asked for the details window. Raised on a worker thread.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Binds the port and starts answering.</summary>
    /// <exception cref="SocketException">The port is taken or cannot be bound. Nothing is left listening.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listeners.Count > 0)
        {
            return;
        }

        // IPv4 is the one that must bind; it decides the port. IPv6 loopback is
        // added on the same port when the machine has it, because the widget asks
        // for "localhost" and Windows resolves that to ::1 before 127.0.0.1.
        // Chromium falls back to IPv4 on its own, but not every client does.
        var ipv4 = new TcpListener(IPAddress.Loopback, _requestedPort);
        ipv4.Start(backlog: 16);
        _listeners.Add(ipv4);
        Port = ((IPEndPoint)ipv4.LocalEndpoint).Port;

        if (Socket.OSSupportsIPv6)
        {
            var ipv6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            try
            {
                ipv6.Start(backlog: 16);
                _listeners.Add(ipv6);
            }
            catch (SocketException)
            {
                // Something else holds the IPv6 side of the port, or the stack is
                // disabled. IPv4 alone is what every consumer falls back to.
            }
        }

        _stopping = new CancellationTokenSource();
        foreach (TcpListener listener in _listeners)
        {
            _accepting.Add(AcceptAsync(listener, _stopping.Token));
        }

        _log?.LogInformation(
            "Usage export listening on ws://localhost:{Port}{Path} and http://localhost:{Port}{Path} ({Families}).",
            Port,
            UsagePath,
            Port,
            UsagePath,
            _listeners.Count == 2 ? "IPv4 and IPv6" : "IPv4");
    }

    /// <summary>Stops answering, drops every subscriber and releases the port. Safe to call twice.</summary>
    public void Stop()
    {
        if (_listeners.Count == 0)
        {
            return;
        }

        _stopping?.Cancel();
        foreach (TcpListener listener in _listeners)
        {
            listener.Stop();
        }

        _listeners.Clear();

        Subscriber[] subscribers;
        lock (_subscribers)
        {
            subscribers = [.. _subscribers];
            _subscribers.Clear();
        }

        foreach (Subscriber subscriber in subscribers)
        {
            subscriber.Abort();
        }

        try
        {
            Task.WaitAll([.. _accepting], TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfacing through Wait; the loops are done either way.
        }

        _accepting.Clear();
        _stopping?.Dispose();
        _stopping = null;
        _log?.LogInformation("Usage export stopped.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private static byte[] Serialize(UsageExport export)
        => JsonSerializer.SerializeToUtf8Bytes(export, ClaudeStatusJsonContext.Default.UsageExport);

    private async Task AcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                Request? request;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(RequestTimeout);
                    request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                }

                if (request is not null
                    && request.WebSocketKey is not null
                    && request.Path == UsagePath
                    && request.Method == "GET"
                    && !IsWebOrigin(request.Origin))
                {
                    // The socket lives as long as the subscriber does: the server's
                    // token, not the request timeout.
                    await ServeSocketAsync(stream, request, ct).ConfigureAwait(false);
                    return;
                }

                byte[] response = request is null ? Respond(400, "Bad Request", cors: false) : Route(request);
                if (request is not null)
                {
                    _log?.LogDebug(
                        "Usage export {Method} {Target} -> {Status}. Headers: {Headers}",
                        request.Method,
                        request.Target,
                        Encoding.ASCII.GetString(response, 9, 3),
                        request.Headers);
                }

                await stream.WriteAsync(response, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
            {
                // A consumer that hung up, or a request that never finished. Nothing
                // to keep: the next connection starts afresh.
            }
        }
    }

    /// <summary>Completes the handshake and keeps the subscriber fed until it leaves.</summary>
    private async Task ServeSocketAsync(NetworkStream stream, Request request, CancellationToken ct)
    {
        // SHA-1 is what RFC 6455 §4.2.2 prescribes for the accept token. It is a
        // protocol handshake value, not a signature or a secret; there is no
        // stronger option a browser would understand.
#pragma warning disable CA5350
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.WebSocketKey + WebSocketGuid)));
#pragma warning restore CA5350
        byte[] handshake = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        await stream.WriteAsync(handshake, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        using WebSocket socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, KeepAlive);
        using var subscriber = new Subscriber(socket);
        int count;
        lock (_subscribers)
        {
            _subscribers.Add(subscriber);
            count = _subscribers.Count;
        }

        _log?.LogInformation("Usage export subscriber connected (origin {Origin}); {Count} connected.", request.Origin ?? "(none)", count);

        try
        {
            UsageExport? latest = Latest;
            if (latest is not null)
            {
                await subscriber.SendAsync(Serialize(latest), ct).ConfigureAwait(false);
            }

            byte[] buffer = new byte[MaxMessageBytes];
            int diagnostics = 0;
            bool whole = true;
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false);
                    break;
                }

                // Only a whole text message that fits the buffer is looked at. A
                // longer one is not something this protocol has, so its pieces
                // are read and dropped.
                bool fits = whole && result.EndOfMessage;
                whole = result.EndOfMessage;
                if (!fits || result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                string text = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
                if (string.Equals(text, OpenCommand, StringComparison.Ordinal))
                {
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (text.StartsWith(LogPrefix, StringComparison.Ordinal) && diagnostics < MaxDiagnosticsPerSubscriber)
                {
                    // The device has no console. A widget that cannot say what it
                    // was handed cannot be debugged, so it may write a few lines
                    // here - capped per connection, control characters removed,
                    // and through the same redacting logger as everything else.
                    diagnostics++;
                    _log?.LogInformation("Usage export subscriber says: {Text}", Printable(text[LogPrefix.Length..]));
                }
            }
        }
        finally
        {
            lock (_subscribers)
            {
                _subscribers.Remove(subscriber);
                count = _subscribers.Count;
            }

            _log?.LogInformation("Usage export subscriber left; {Count} connected.", count);
        }
    }

    /// <summary>Pushes a document to every subscriber. A send that fails drops that subscriber.</summary>
    private void Broadcast(byte[] payload)
    {
        Subscriber[] subscribers;
        lock (_subscribers)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }

            subscribers = [.. _subscribers];
        }

        foreach (Subscriber subscriber in subscribers)
        {
            _ = subscriber.SendAsync(payload, CancellationToken.None);
        }
    }

    /// <summary>One connected socket and the lock that keeps its frames whole.</summary>
    private sealed class Subscriber(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public void Dispose() => _gate.Dispose();

        public async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The receive loop sees the same failure and takes the subscriber
                // off the list; aborting here just makes that happen now.
                socket.Abort();
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Abort() => socket.Abort();
    }

    /// <summary>The parts of a request this server looks at.</summary>
    /// <param name="Target">The request target as sent, query string included, for the log.</param>
    /// <param name="Headers">Every request header, for the log: loopback traffic from a page, nothing secret in it.</param>
    /// <param name="WebSocketKey">The Sec-WebSocket-Key when the request asks to upgrade, else null.</param>
    private sealed record Request(string Method, string Path, string? Origin, string Target, string Headers, string? WebSocketKey);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[MaxRequestBytes];
        int length = 0;
        int headersEnd = -1;

        while (headersEnd < 0 && length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            headersEnd = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
        }

        if (headersEnd < 0)
        {
            return null;
        }

        string head = Encoding.ASCII.GetString(buffer, 0, headersEnd);
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3)
        {
            return null;
        }

        string? origin = null;
        string? key = null;
        bool upgrade = false;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            ReadOnlySpan<char> name = line.AsSpan(0, colon).Trim();
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("origin", StringComparison.OrdinalIgnoreCase))
            {
                origin = value;
            }
            else if (name.Equals("sec-websocket-key", StringComparison.OrdinalIgnoreCase))
            {
                key = value;
            }
            else if (name.Equals("upgrade", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = value.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            }
        }

        string path = requestLine[1];
        int query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        return new Request(
            requestLine[0].ToUpperInvariant(),
            path,
            origin,
            requestLine[1],
            string.Join(" | ", lines.Skip(1)),
            upgrade ? key : null);
    }

    private byte[] Route(Request request)
    {
        // A web page on this machine asking. Whatever it asked for, it gets no
        // body and no permission to read one. Logged once per origin: the one
        // time this matters is when the widget itself is being refused, and the
        // log is then the only place that says why.
        if (IsWebOrigin(request.Origin))
        {
            bool first;
            lock (_refusedOrigins)
            {
                first = _refusedOrigins.Add(request.Origin!);
            }

            if (first)
            {
                _log?.LogWarning("Usage export refused a request from web origin {Origin}.", request.Origin);
            }

            return Respond(403, "Forbidden", cors: false);
        }

        if (request.Method == "OPTIONS")
        {
            return Respond(204, "No Content", cors: true, origin: request.Origin);
        }

        switch (request.Path)
        {
            case UsagePath when request.Method is "GET" or "HEAD":
                UsageExport? latest = Latest;
                return latest is null
                    ? Respond(503, "Service Unavailable", cors: true, origin: request.Origin)
                    : Respond(200, "OK", cors: true, Serialize(latest), request.Method == "HEAD", request.Origin);

            case HealthPath when request.Method is "GET" or "HEAD":
                return Respond(200, "OK", cors: true, HealthBody, request.Method == "HEAD", request.Origin);

            case OpenPath when request.Method == "POST":
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return Respond(204, "No Content", cors: true, origin: request.Origin);

            case UsagePath or HealthPath or OpenPath:
                return Respond(405, "Method Not Allowed", cors: true, origin: request.Origin);

            default:
                return Respond(404, "Not Found", cors: true, origin: request.Origin);
        }
    }

    /// <summary>A subscriber's text with anything that could forge or break a log line taken out.</summary>
    internal static string Printable(string text)
        => string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });

    /// <summary>Whether an Origin header names a web page rather than an embedded or local document.</summary>
    internal static bool IsWebOrigin(string? origin)
        => origin is not null
           && (origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static byte[] Respond(int status, string reason, bool cors, byte[]? body = null, bool headOnly = false, string? origin = null)
    {
        var head = new StringBuilder(256)
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Date: ").Append(DateTime.UtcNow.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n");

        if (cors)
        {
            // The origin is echoed rather than answered with "*", so an opaque
            // origin ("null", a page loaded from disk) gets the exact match every
            // CORS implementation accepts. Only non-web origins get here (see
            // Route), so echoing one grants nothing a wildcard would not.
            //
            // Allow-Private-Network: Chromium's Private Network Access check
            // preflights a request from a page to a loopback address and expects
            // this answer, or blocks the request before it is ever sent.
            head.Append("Access-Control-Allow-Origin: ").Append(string.IsNullOrEmpty(origin) ? "*" : origin).Append("\r\n")
                .Append("Vary: Origin\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n")
                .Append("Access-Control-Allow-Headers: Content-Type\r\n")
                .Append("Access-Control-Allow-Private-Network: true\r\n")
                .Append("Access-Control-Max-Age: 600\r\n");
        }

        if (body is not null)
        {
            head.Append("Content-Type: application/json; charset=utf-8\r\n")
                .Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }
        else
        {
            head.Append("Content-Length: 0\r\n");
        }

        head.Append("\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        if (body is null || headOnly)
        {
            return headBytes;
        }

        byte[] response = new byte[headBytes.Length + body.Length];
        headBytes.CopyTo(response, 0);
        body.CopyTo(response, headBytes.Length);
        return response;
    }
}
