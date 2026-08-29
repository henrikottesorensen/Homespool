using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.FakePrinter;

/// <summary>
/// One fake printer: enrols through the real code-exchange flow (or accepts a pre-provisioned
/// token), connects <c>/p/ws</c> the way Buddy does, streams telemetry from an
/// <see cref="ITelemetrySource"/>, and answers server commands through a
/// <see cref="CommandAnswerPolicy"/>. Drives a running server via the default
/// <see cref="ClientWebSocket"/> connector, or an in-process <c>TestServer</c> via a substituted
/// <see cref="WebSocketConnector"/> - same fake, both modes.
/// </summary>
/// <remarks>
/// Wire fidelity notes: the register body carries the full 50-character fingerprint while the
/// upgrade header carries the 16-character truncation (see <see cref="PrinterIdentity"/>); every
/// outgoing message is fragmented at <see cref="FakePrinterOptions.SendFragmentSize"/> (512, like
/// the firmware's render buffer) as Text-plus-continuations; an <c>INFO</c> event is sent first on
/// every connection, because <c>Planner::reset()</c> guarantees exactly that.
/// </remarks>
public sealed class FakePrinterClient : IAsyncDisposable
{
    private const string SubProtocol = "prusa-connect";

    private readonly FakePrinterOptions _options;
    private readonly CommandAnswerPolicy _policy;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<ServerCommandFrame> _receivedCommands = [];
    private readonly TaskCompletionSource _telemetryCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebSocket? _socket;
    private Exception? _replyFault;

    /// <summary>Creates a fake with the given identity; null options take every firmware default.</summary>
    public FakePrinterClient(PrinterIdentity identity,
                             TimeProvider timeProvider,
                             FakePrinterOptions? options = null)
    {
        Identity = identity;
        _options = options ?? new FakePrinterOptions();
        _policy = _options.Policy ?? new FirmwareFaithfulPolicy(identity, timeProvider);
    }

    /// <summary>The identity this fake presents on the wire.</summary>
    public PrinterIdentity Identity { get; }

    /// <summary>The device-side state machine; set it up (e.g. <see cref="FakeDevice.StartPrint"/>) before connecting.</summary>
    public FakeDevice Device { get; } = new();

    /// <summary>
    /// The bearer token presented on the upgrade. Set by <see cref="EnrolAsync"/>, or directly for
    /// the USB-provisioning-shaped flow where the token exists before first contact.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Completes when the telemetry source is exhausted - lets a replay test await "all sent".</summary>
    public Task TelemetryCompleted => _telemetryCompleted.Task;

    /// <summary>Every parsed command frame the server has sent this connection, oldest first.</summary>
    public IReadOnlyList<ServerCommandFrame> ReceivedCommands
    {
        get
        {
            lock (_receivedCommands)
            {
                return _receivedCommands.ToArray();
            }
        }
    }

    /// <summary>
    /// The first exception a background reply task hit, if any - a faulted reply would otherwise
    /// be invisible, and a test asserting on server behaviour should be able to rule out a broken
    /// fake first.
    /// </summary>
    public Exception? ReplyFault => _replyFault;

    /// <summary>
    /// <c>POST /p/register</c> exactly as Buddy sends it: <c>User-Agent-Printer</c>/<c>-Version</c>
    /// headers, JSON body with the <b>full</b> fingerprint (registrator.cpp:61 - the one body-not-
    /// header transmission), no <c>Fingerprint</c> header. Returns the registration code.
    /// </summary>
    /// <param name="httpClient">Client with its <c>BaseAddress</c> pointing at the server.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<string> RegisterAsync(HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/p/register");
        AddUserAgentHeaders(request);
        request.Content = JsonContent.Create(new
        {
            sn = Identity.SerialNumber,
            fingerprint = Identity.Fingerprint,
            printer_type = Identity.PrinterType,
            firmware = Identity.Firmware,
        });

        HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return response.Headers.GetValues("Code").First();
    }

    /// <summary>
    /// One <c>GET /p/register</c> poll with the <c>Code</c> header (Buddy's <c>PollRequest</c>
    /// sends only that). Returns the token on 200, null while the claim is pending (202).
    /// </summary>
    public async Task<string?> PollForTokenOnceAsync(HttpClient httpClient,
                                                     string code,
                                                     CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/p/register");
        AddUserAgentHeaders(request);
        request.Headers.TryAddWithoutValidation("Code", code);

        HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return response.Headers.GetValues("Token").First();
    }

    /// <summary>
    /// Polls until a user claims the code and a token is issued, then stores it in
    /// <see cref="Token"/>. The firmware polls every 5 s, forever; pass a shorter interval in tests.
    /// </summary>
    public async Task<string> EnrolAsync(HttpClient httpClient,
                                         string code,
                                         TimeSpan pollInterval,
                                         CancellationToken cancellationToken = default)
    {
        while (true)
        {
            string? token = await PollForTokenOnceAsync(httpClient, code, cancellationToken);

            if (token is not null)
            {
                Token = token;

                return token;
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Performs the WebSocket upgrade with Buddy's exact header set: the <b>16-character</b>
    /// fingerprint and the token (connect.cpp:160-171, <c>UpgradeRequest</c>), plus the
    /// <c>User-Agent-Printer</c>/<c>-Version</c> pair the HTTP client layer stamps on <b>every</b>
    /// request (httpc.cpp:218-219 - a common-header layer above each request's
    /// <c>extra_headers()</c>, easy to miss when reading only the request classes), and the
    /// <c>prusa-connect</c> subprotocol.
    /// </summary>
    /// <param name="connector">
    /// How to reach the server; null uses a real <see cref="ClientWebSocket"/> against
    /// <see cref="FakePrinterOptions.BaseAddress"/>. In-process tests pass a
    /// <c>TestServer</c>-backed connector instead.
    /// </param>
    /// <param name="cancellationToken">Cancels the upgrade.</param>
    public async Task ConnectAsync(WebSocketConnector? connector = null, CancellationToken cancellationToken = default)
    {
        if (Token is null)
        {
            throw new InvalidOperationException("No token - enrol first or set Token directly.");
        }

        Dictionary<string, string> headers = new()
        {
            ["Fingerprint"] = Identity.HeaderFingerprint,
            ["Token"] = Token,
            ["User-Agent-Printer"] = _options.UserAgentPrinter,
            ["User-Agent-Version"] = Identity.Firmware,
        };

        FakePrinterConnectRequest request = new(BuildWebSocketUri(), SubProtocol, headers);
        _socket = await (connector ?? ConnectWithClientWebSocketAsync)(request, cancellationToken);
    }

    /// <summary>
    /// Runs the connection: INFO first (guaranteed on every real connection - planner.cpp:347),
    /// then the telemetry loop and the command-answering read loop until cancellation, close, or
    /// disconnect. A finite telemetry source ending does not end the run; commands are still
    /// answered until the socket goes away.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        WebSocket socket = _socket ?? throw new InvalidOperationException("Not connected - call ConnectAsync first.");

        await SendMessageAsync(EventMessageBuilder.BuildInfo(Identity, Device.WireState, null, Device.JobId, Device.FreeSpace),
                               cancellationToken);

        Task read = ReadLoopAsync(socket, cancellationToken);
        Task telemetry = TelemetryLoopAsync(socket, cancellationToken);

        await Task.WhenAll(read, telemetry);
    }

    /// <summary>
    /// Runs the <b>pre-websocket HTTP transport</b>: INFO, then the telemetry source, each message
    /// its own POST. Ends when the source runs out or the token is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No connection and no read loop</b>, so <see cref="ConnectAsync"/> is not called and
    /// <see cref="CommandAnswerPolicy"/> never runs. On this transport a command travels in the
    /// response to a telemetry POST, and Homespool answers 204 - nothing pending - always. A fake
    /// that pretended otherwise would be modelling a server that does not exist.
    /// </para>
    /// <para>
    /// <b>The route is chosen per message, which is this transport's one structural difference.</b>
    /// A socket carries events and telemetry down the same pipe and the server sorts them by
    /// content; here they are two URLs, so the client must sort them instead - an event-mixing
    /// telemetry source emits both shapes from one sequence.
    /// </para>
    /// </remarks>
    /// <param name="httpClient">Addresses the printer listener; the same client the enrol calls use.</param>
    /// <param name="cancellationToken">Ends the run.</param>
    public async Task RunHttpAsync(HttpClient httpClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (Token is null)
        {
            throw new InvalidOperationException("No token - enrol first or set Token directly.");
        }

        await PostMessageAsync(
            httpClient,
            EventMessageBuilder.BuildInfo(Identity, Device.WireState, null, Device.JobId, Device.FreeSpace),
            cancellationToken);

        if (_options.TelemetrySource is null)
        {
            _telemetryCompleted.TrySetResult();

            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // A device event the printer decided to send - an attention it just raised -
                // goes ahead of the timed telemetry, which is the order a real one produces: the
                // state change is reported when it happens, not at the next tick.
                byte[]? next = Device.PendingEvents.Count > 0
                    ? Device.PendingEvents.Dequeue()
                    : _options.TelemetrySource.NextMessage(Device);

                if (next is null)
                {
                    break;
                }

                ServerCommandFrame? command = await PostMessageAsync(httpClient, next, cancellationToken);

                if (command is not null)
                {
                    // Answered the way a socket-delivered command is: same policy, same recording,
                    // the reply going out as its own POST. No connection to drop, so a policy that
                    // asks for one faults rather than pretends.
                    HandleCommand(command,
                                  (payload, ct) => PostMessageAsync(httpClient, payload, ct),
                                  disconnect: null,
                                  cancellationToken);
                }

                TimeSpan delay = _options.TelemetrySource.DelayBeforeNext(Device);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not failure.
        }
        finally
        {
            _telemetryCompleted.TrySetResult();
        }
    }

    /// <summary>
    /// Client-initiated clean close. Sends the close frame only (<c>CloseOutputAsync</c>) so the
    /// concurrent read loop is the one that observes the server's answering close.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutting down", cancellationToken);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        _sendLock.Dispose();

        return ValueTask.CompletedTask;
    }

    private static bool IsConnectionGone(Exception exception)
    {
        return exception is WebSocketException or ObjectDisposedException or IOException;
    }

    /// <summary>
    /// Posts one already-rendered message to whichever of the two endpoints its shape belongs to,
    /// with the four headers the server's authentication requires on every request - and returns
    /// the command the response carried, if it carried one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A non-success status is thrown rather than retried: a fake that quietly swallows a 401 or a
    /// 400 looks exactly like one that is working, which is the failure this whole harness exists to
    /// avoid. The real printer retries; a test double should stop and say so.
    /// </para>
    /// <para>
    /// A 200 is read the way firmware reads it (<c>handle_server_resp</c>, connect.cpp:212-265 at
    /// v6.2.6): the id from the <c>Command-Id</c> header, base ten; the kind from <c>Content-Type</c>,
    /// JSON or gcode; anything else an unknown command. A 200 with no <c>Command-Id</c> is the
    /// server's error, and firmware treats it as one - it discards the body and invalidates the
    /// connection - so it is thrown here rather than tolerated.
    /// </para>
    /// </remarks>
    private async Task<ServerCommandFrame?> PostMessageAsync(HttpClient httpClient, byte[] payload, CancellationToken cancellationToken)
    {
        string route = IsEventMessage(payload) ? "/p/events" : "/p/telemetry";

        using HttpRequestMessage request = new(HttpMethod.Post, route);

        request.Headers.TryAddWithoutValidation("Fingerprint", Identity.HeaderFingerprint);
        request.Headers.TryAddWithoutValidation("Token", Token);
        AddUserAgentHeaders(request);

        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

        response.EnsureSuccessStatusCode();

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        if (!response.Headers.TryGetValues("Command-Id", out IEnumerable<string>? values)
            || !uint.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out uint commandId))
        {
            throw new InvalidOperationException(
                "The server answered 200 to a telemetry POST without a Command-Id header, which firmware would discard as confused.");
        }

        ServerCommandKind kind = response.Content.Headers.ContentType?.MediaType switch
        {
            MediaTypeNames.Application.Json => ServerCommandKind.Json,
            "text/x.gcode" or "text/x-gcode" => ServerCommandKind.Gcode,
            _ => ServerCommandKind.Undefined,
        };

        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        return new ServerCommandFrame(kind, commandId, body);
    }

    /// <summary>
    /// Whether a rendered message is an event, by the same test the server's dispatcher applies -
    /// the presence of an <c>event</c> property.
    /// </summary>
    private static bool IsEventMessage(byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);

        return document.RootElement.TryGetProperty("event", out _);
    }

    private void AddUserAgentHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent-Printer", _options.UserAgentPrinter);
        request.Headers.TryAddWithoutValidation("User-Agent-Version", Identity.Firmware);
    }

    private Uri BuildWebSocketUri()
    {
        if (_options.BaseAddress is null)
        {
            // A custom connector may ignore the Uri entirely (TestServer's does not care about the
            // host), but there is nothing sensible to build without a base.
            return new Uri("ws://localhost/p/ws");
        }

        UriBuilder builder = new(_options.BaseAddress)
        {
            Scheme = _options.BaseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/p/ws",
        };

        return builder.Uri;
    }

    private async Task<WebSocket> ConnectWithClientWebSocketAsync(FakePrinterConnectRequest request,
                                                                  CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        socket.Options.AddSubProtocol(request.SubProtocol);

        // The ping compromise: .NET cannot send explicit Ping
        // frames, so Buddy's 15s-inactivity ping / 60s socket timeout pair is approximated with
        // the managed keep-alive.
        socket.Options.KeepAliveInterval = _options.KeepAliveInterval;
        socket.Options.KeepAliveTimeout = _options.KeepAliveTimeout;

        foreach (KeyValuePair<string, string> header in request.Headers)
        {
            socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        try
        {
            await socket.ConnectAsync(request.Uri, cancellationToken);

            return socket;
        }
        catch
        {
            socket.Dispose();

            throw;
        }
    }

    private async Task ReadLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        ArrayBufferWriter<byte> message = new();

        try
        {
            while (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseSent)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                    }

                    return;
                }

                message.Write(buffer.AsSpan(0, result.Count));

                if (!result.EndOfMessage)
                {
                    continue;
                }

                byte[] frameBytes = message.WrittenSpan.ToArray();
                message.ResetWrittenCount();
                await HandleFrameAsync(socket, frameBytes, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not failure.
        }
        catch (Exception exception) when (IsConnectionGone(exception))
        {
            // The connection dropped under us - for a test double that is an ordinary way to end.
        }
    }

    private async Task HandleFrameAsync(WebSocket socket, byte[] frameBytes, CancellationToken cancellationToken)
    {
        FrameParseResult parsed = ServerCommandFrame.Parse(frameBytes);

        if (parsed.Broken is not null)
        {
            // The firmware plans a REJECTED with the BrokenCommand reason (planner.cpp:671-673).
            await SendMessageAsync(
                EventMessageBuilder.Build("REJECTED", Device.WireState, parsed.Broken.CommandId, parsed.Broken.Reason),
                cancellationToken);

            return;
        }

        HandleCommand(parsed.Frame!, SendMessageAsync, socket.Abort, cancellationToken);
    }

    /// <summary>
    /// What both transports do with a command once they have one: record it, ask the policy, and run
    /// the replies off the receiving path. The two verbs a reply needs - send this payload, drop the
    /// connection - are what differ between transports, so they are passed in rather than assumed.
    /// </summary>
    /// <param name="frame">The command, however it arrived.</param>
    /// <param name="send">Sends one rendered message: a socket write, or a POST.</param>
    /// <param name="disconnect">
    /// Drops the connection, for the policies that ask to. Null when the transport has no connection
    /// to drop - the HTTP transport - in which case a policy asking for it gets nothing, and says so
    /// through <see cref="ReplyFault"/> rather than silently.
    /// </param>
    /// <param name="cancellationToken">Ends any delayed reply.</param>
    private void HandleCommand(ServerCommandFrame frame,
                               Func<byte[], CancellationToken, Task> send,
                               Action? disconnect,
                               CancellationToken cancellationToken)
    {
        lock (_receivedCommands)
        {
            _receivedCommands.Add(frame);
        }

        IReadOnlyList<PlannedReply> replies = _policy.Answer(frame, Device);

        if (replies.Count == 0)
        {
            return;
        }

        // Replies run off the receiving path so the fake keeps receiving while a delayed reply is
        // pending - the firmware likewise keeps receiving (and rejecting) commands while a background
        // command executes. On the socket the send lock keeps whole messages from interleaving.
        _ = ExecuteRepliesAsync(replies, send, disconnect, cancellationToken);
    }

    private async Task ExecuteRepliesAsync(IReadOnlyList<PlannedReply> replies,
                                           Func<byte[], CancellationToken, Task> send,
                                           Action? disconnect,
                                           CancellationToken cancellationToken)
    {
        try
        {
            foreach (PlannedReply reply in replies)
            {
                if (reply.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(reply.Delay, cancellationToken);
                }

                if (reply.Payload is not null)
                {
                    await send(reply.Payload, cancellationToken);
                }

                if (reply.DisconnectAfter)
                {
                    if (disconnect is null)
                    {
                        // A DisconnectOnCommandPolicy on a transport with nothing to disconnect. Not
                        // an error of the fake's, but a test that asked for it would otherwise pass
                        // on a behaviour that never happened.
                        throw new InvalidOperationException(
                            "The policy asked to disconnect, but the HTTP transport has no connection to drop.");
                    }

                    disconnect();

                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not failure.
        }
        catch (Exception exception) when (IsConnectionGone(exception))
        {
            // Connection ended mid-reply; nothing to do.
        }
        catch (Exception exception)
        {
            // A genuinely broken fake (builder bug, policy bug) must be visible to the test that
            // is otherwise asserting on server behaviour.
            Interlocked.CompareExchange(ref _replyFault, exception, null);
        }
    }

    private async Task TelemetryLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        if (_options.TelemetrySource is null)
        {
            _telemetryCompleted.TrySetResult();

            return;
        }

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                // See the HTTP loop: a raised attention is sent when it is raised.
                byte[]? next = Device.PendingEvents.Count > 0
                    ? Device.PendingEvents.Dequeue()
                    : _options.TelemetrySource.NextMessage(Device);

                if (next is null)
                {
                    break;
                }

                await SendMessageAsync(next, cancellationToken);

                TimeSpan delay = _options.TelemetrySource.DelayBeforeNext(Device);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not failure.
        }
        catch (Exception exception) when (IsConnectionGone(exception))
        {
            // The connection dropped under us; the source simply stops.
        }
        finally
        {
            _telemetryCompleted.TrySetResult();
        }
    }

    private async Task SendMessageAsync(byte[] payload, CancellationToken cancellationToken)
    {
        WebSocket socket = _socket ?? throw new InvalidOperationException("Not connected.");

        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            // One JSON document per WebSocket message, fragmented at the firmware's buffer size:
            // Text first, continuations after, fin on the last (connect.cpp:646-673). The managed
            // WebSocket emits continuation opcodes for the non-first sends on its own.
            int offset = 0;

            while (offset < payload.Length)
            {
                int count = Math.Min(_options.SendFragmentSize, payload.Length - offset);
                bool last = offset + count >= payload.Length;

                await socket.SendAsync(
                    new ArraySegment<byte>(payload, offset, count),
                    WebSocketMessageType.Text,
                    last,
                    cancellationToken);

                offset += count;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
