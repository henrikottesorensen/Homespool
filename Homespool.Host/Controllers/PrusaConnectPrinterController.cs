using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO;

// What both HTTP-transport ingest endpoints answer, and the helper behind them: named once,
// because a union spelled out at three sites is three chances for one to drift. The file arm is a
// command handed back on the response to a telemetry post - the pre-websocket transport's only way
// of delivering one.
using IngestResult =
    Microsoft.AspNetCore.Http.HttpResults.Results<
        Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult,
        Microsoft.AspNetCore.Http.HttpResults.NoContent,
        Microsoft.AspNetCore.Http.HttpResults.BadRequest>;

namespace Homespool.Host.Controllers;

[ApiController]
[Authorize(Authorisation.Policies.PrusaConnectPrinter)]
public class PrusaConnectPrinterController : ControllerBase
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly PrinterConnectionSession _session;
    private readonly MessageDispatcher _dispatcher;
    private readonly HttpPrinterSessions _sessions;
    private readonly PrusaConnect.Transfers.ITransferContentStore _content;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly PrusaConnectOptions _options;
    private readonly ILogger<PrusaConnectPrinterController> _logger;

    public PrusaConnectPrinterController(PrusaConnectService prusaConnectService,
                                         PrinterConnectionSession session,
                                         MessageDispatcher dispatcher,
                                         HttpPrinterSessions sessions,
                                         PrusaConnect.Transfers.ITransferContentStore content,
                                         IHostApplicationLifetime lifetime,
                                         IOptions<PrusaConnectOptions> options,
                                         ILogger<PrusaConnectPrinterController> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _prusaConnectService = prusaConnectService;
        _session = session;
        _dispatcher = dispatcher;
        _sessions = sessions;
        _content = content;
        _lifetime = lifetime;
        _options = options.Value;
        _logger = logger;
    }

    // [HttpGet] as well as the route, so this reaches the OpenAPI document at all: ApiExplorer cannot
    // describe an action with no method constraint, which is why /p/ws was the one printer endpoint
    // missing from it. A WebSocket upgrade is a GET, so this also narrows the routing to what the
    // protocol actually uses (Henrik, 2026-08-01, after the change was flagged as touching /p/*).
    [HttpGet]
    [Route("/p/ws")]
    [EnableRateLimiting(Program.PrinterSocketRateLimitPolicy)]

    // The 101 is said here because nothing in the union can: the response starts inside the action.
    [ProducesResponseType(typeof(void), StatusCodes.Status101SwitchingProtocols)]
    public async Task<Results<EmptyHttpResult, BadRequest>> ConnectWebSocket()
    {
        try
        {
            PrinterClientHeaders clientHeaders = new(Request);

            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                // Guaranteed present: [Authorize] above only lets the request through once
                // PrusaConnectPrinterAuthenticationHandler has already resolved the Fingerprint
                // header to a Printer and issued this claim.
                int printerId = int.Parse(User.FindFirstValue(HSClaimTypes.PrinterId)!);

                using WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    SubProtocol = Headers.Values.WSProtocolPrusaConnect,
                    KeepAliveInterval = TimeSpan.FromSeconds(120),
                    KeepAliveTimeout = TimeSpan.FromSeconds(120),
                });

                _logger.LogDebug("Connected websocket from {Client}:{Port} {Printer} {Fingerprint} {PrinterId}",
                                 HttpContext.Connection.RemoteIpAddress,
                                 HttpContext.Connection.RemotePort,
                                 clientHeaders.Printer,
                                 clientHeaders.FingerPrint,
                                 printerId);

                // The read side: WebSocketStream (.NET 10) presents the socket as a plain byte
                // stream - EOF at the peer's close frame - and StreamPipeReader gives the handler
                // the PipeReader it parses from. Reads pull straight from the socket, so there is
                // no pump task to run alongside the handler. Writes don't go through the stream at
                // all (WebSocketPrinterConnection sends on the socket directly), so the message
                // type here is inert; ownsWebSocket stays false because the close handshake is the
                // session's, below.
                await using Stream socketStream = WebSocketStream.Create(webSocket, WebSocketMessageType.Binary);
                PipeReader input = PipeReader.Create(socketStream, new StreamPipeReaderOptions(leaveOpen: true));

                // No transport argument any more. It used to take Request.IsHttps to size frames for
                // the printer's TLS record buffer; nginx terminates that TLS now and OpenSSL honours
                // the max_fragment_length the printer negotiates, so this socket is plain HTTP to the
                // proxy and record size is not this process's business. See WebSocketPrinterConnection
                // .MaxFramePayload - including why putting the old sizing back would hide a real
                // failure rather than fix one.
                WebSocketPrinterConnection connection = new(webSocket);

                // Two reasons this read loop should stop, neither of them the printer's doing:
                // RequestAborted (the client vanished, or Kestrel aborted us) and ApplicationStopping.
                // The second is the load-bearing one: a printer connection is idle-but-open for hours,
                // so without it every shutdown parked here until Kestrel's timeout expired and killed
                // the request - a ~30s stall that invites operators to SIGKILL instead, which is how
                // buffered telemetry actually gets lost. Cancelling the receive aborts the socket, so
                // the printer sees the connection drop and reconnects on its own retry timer.
                using CancellationTokenSource connectionLifetime =
                    CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, _lifetime.ApplicationStopping);

                // Everything from here - register, read loop, ordered teardown, close - belongs to
                // the session, which owns `input` from this point and completes it. It is a separate
                // type purely so that sequence is reachable without an HttpContext, and so a test
                // can pin the order each of its steps was bought with.
                await _session.RunAsync(printerId, connection, input, connectionLifetime.Token);

                // Not Ok(): the response started at the 101, and a status-code result sets
                // Response.StatusCode during result execution - after this action returns, outside
                // the try above - which throws "the response has already started" and surfaces as an
                // unhandled error the client never sees, because the socket is closed by then.
                return TypedResults.Empty;
            }
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.BadRequest();
    }

    [AllowAnonymous]
    [EnableRateLimiting(Program.PrinterRegistrationRateLimitPolicy)]
    [HttpPost]
    [Route("/p/register")]

    // Eight kilobytes for four short strings, and the cap is the point: this action is anonymous, so
    // without one a caller chooses how much of the listener's thirty-odd megabytes to spend per
    // request. The rate limiter bounds how many requests arrive, never how large they are. Generous
    // enough that no real printer can meet it - see RegisterPrinterRequestDTO for the measured shapes
    // and for why refusing a real one is expensive.
    [RequestSizeLimit(8 * 1024)]

    // Firmware reads the status code and the Code header; the body is deliberately empty, and
    // text/html rather than JSON because that is what Connect answers with. The 200 is said here
    // because a content result carries no metadata of its own.
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    public async Task<Results<ContentHttpResult, BadRequest>> RegisterPrinter([FromBody] RegisterPrinterRequestDTO printer)
    {
        try
        {
            // [FromBody] is required. Without it - and without [ApiController], which is deliberately
            // not used here (see ApiExplorerVisibilityConvention) - MVC binds complex parameters from
            // form data, not the JSON body, leaving every property null. The insert then died on
            // NOT NULL SerialNumber. Everything this action needs is in the body; the printer sends
            // no headers at all on this request.

            // Get code for printer.
            CodeResponseDTO code = await _prusaConnectService.GetPrinterCode(printer);

            // Apparently we return all the data in headers?
            Response.Headers.TryAdd(Headers.Code, code.TemporaryCode);
            Response.Headers.TryAdd(Headers.TemporaryCode, code.TemporaryCode);

            Response.Headers.TryAdd(Headers.Expires, $"{code.Expires:R}");

            return TypedResults.Content(string.Empty, MediaTypeNames.Text.Html);
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return TypedResults.BadRequest();
        }
    }

    [AllowAnonymous]
    [EnableRateLimiting(Program.PrinterRegistrationRateLimitPolicy)]
    [HttpGet]
    [Route("/p/register")]
    [RequestSizeLimit(8 * 1024)]

    // 200 carries the token in a header and nothing in the body; 202 is the one answer here with a
    // payload, telling the printer to poll again. The 401 is said here because an unauthorized
    // result carries no metadata of its own; the rest say theirs through the union.
    [ProducesResponseType(typeof(void), StatusCodes.Status401Unauthorized)]
    public async Task<Results<Ok, Accepted<MessageDTO>, ContentHttpResult, BadRequest, NotFound, UnauthorizedHttpResult>>
        GetPrinterRegistrationStatus()
    {
        try
        {
            PrinterClientHeaders clientHeaders = new(Request);

            // The printer identifies itself by the code alone here: Buddy's poll carries a Code
            // header and nothing else.
            if (clientHeaders.Code is null)
            {
                return TypedResults.Text("Code missing", statusCode: StatusCodes.Status400BadRequest);
            }

            string? token = await _prusaConnectService.GetToken(clientHeaders.Code);

            if (string.IsNullOrWhiteSpace(token))
            {
                return TypedResults.Accepted((string?)null, new MessageDTO
                {
                    Message = "User hasn't used Temporary-Code yet. Printer must call it one more time",
                    Code = "REGISTRATION_ACCEPTED",
                });
            }

            Response.Headers.TryAdd(Headers.Token, token);
            return TypedResults.Ok();
        }
        catch (Exception e) when (e is ArgumentNullException or InvalidOperationException)
        {
            return TypedResults.BadRequest();
        }
        catch (PrinterNotFoundException)
        {
            return TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    /// <summary>
    /// Receives one telemetry message from a printer speaking the pre-websocket HTTP transport, and
    /// answers it with whatever command is waiting for that printer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response is the command channel.</b> On this transport a command travels in the
    /// response to a telemetry POST: 200 with a <c>Command-Id</c> header and the command as the body,
    /// or 204 for nothing pending (<c>handle_server_resp</c>, connect.cpp:712-745 at v6.2.6). The
    /// header is not optional - a 200 without it makes firmware discard the body, invalidate its
    /// connection and report <c>OnlineError::Confused</c>. Nor is the body's <c>Content-Type</c>: it
    /// is how firmware chooses a parser, and an unknown one becomes an <c>UnknownCommand</c>.
    /// </para>
    /// <para>
    /// Only telemetry carries a command back. An event's response is always 204: firmware reads it
    /// through the same path, but a command riding on an ack would arrive out of the one-in-flight
    /// order the actor keeps, so the parked command waits for the next telemetry poll instead.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Route("/p/telemetry")]
    [EnableRateLimiting(Program.PrinterHttpTransportRateLimitPolicy)]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest)]
    public Task<IngestResult> PostTelemetry(CancellationToken cancellationToken)
    {
        return IngestAsync(deliverCommand: true, cancellationToken);
    }

    /// <summary>
    /// Serves a file to a printer that fetches it over HTTP without decrypting it.
    /// <c>GET /p/teams/{teamId}/files/{hash}/raw</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The path is the Python Connect SDK's, not ours.</b> Its <c>START_CONNECT_DOWNLOAD</c>
    /// handler does not take a URL - it composes one, <c>/p/teams/{team_id}/files/{hash}/raw</c>,
    /// appended to the same server it posts telemetry to. So the shape here is fixed by the client
    /// and matching it exactly is the whole job.
    /// </para>
    /// <para>
    /// <b>Which is also what makes it authenticated.</b> The SDK attaches its <c>Fingerprint</c> and
    /// <c>Token</c> only when the download URL starts with that server address, and strips them for
    /// anywhere else (<c>download.py</c>). Being on this listener, under this controller's
    /// <c>[Authorize]</c>, is therefore not a nicety - move it and the credentials silently stop
    /// arriving.
    /// </para>
    /// <para>
    /// <b><c>teamId</c> is carried and not trusted.</b> It is in the path because the SDK puts it
    /// there; what decides whether these bytes may be served is <c>hash</c>, which is the per-send
    /// random token the offer was made under - unguessable, revoked when the send finishes or fails,
    /// and never derived from the content. A digest would have been a stable URL for the life of the
    /// file, which <c>notes/file-storage.md</c> records as the rejected design's one privacy leak.
    /// </para>
    /// <para>
    /// <b>Plaintext, deliberately, and only to a client that cannot do better.</b> Firmware gets the
    /// AES-CTR download because <c>M997</c> makes an unauthenticated fetch dangerous; this path is
    /// authenticated instead, which is the protection the SDK can actually use.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Route("/p/teams/{teamId:long}/files/{hash}/raw")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    public Results<OfferedFileResult, NotFound> FetchOffered(long teamId, string hash)
    {
        if (!_content.TryOpen(hash, out PrusaConnect.Transfers.ITransferContent? content))
        {
            // Unknown, revoked, or the bytes are gone - one answer, as the encrypted route gives.
            return TypedResults.NotFound();
        }

        // Ownership passes to the result, which disposes it when the body is written or abandoned.
        return new OfferedFileResult(content);
    }

    /// <summary>
    /// Receives one event from a printer speaking the pre-websocket HTTP transport.
    /// </summary>
    [HttpPost]
    [Route("/p/events")]
    [EnableRateLimiting(Program.PrinterHttpTransportRateLimitPolicy)]
    [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest)]
    public Task<IngestResult> PostEvent(CancellationToken cancellationToken)
    {
        return IngestAsync(deliverCommand: false, cancellationToken);
    }

    /// <summary>
    /// Posts one message to the printer's actor, whichever of the two endpoints it arrived on, and
    /// - for telemetry - collects the command the actor has parked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One body for both routes, because the message decides what it is.</b>
    /// <see cref="MessageDispatcher.Classify"/> sorts on the payload's own <c>event</c> property and
    /// is the only place that decision is made; keying off the URL instead would be a second
    /// classifier free to disagree with it.
    /// </para>
    /// <para>
    /// <b>Through the actor, not straight to the sink.</b> The actor is where an event answering a
    /// command meets the command it answers (<c>HandleEvent</c>), and where the last point that knows
    /// this printer speaks Prusa Connect maps into the sink. Bypassing it would persist the ack and
    /// leave the caller of the command waiting out the response timeout.
    /// </para>
    /// <para>
    /// <b>The session, not the connection, decides whether this printer has an actor.</b>
    /// <see cref="HttpPrinterSessions.GetOrCreate"/> creates one on the first authenticated POST and
    /// touches it on every one after, which is what makes the printer read as connected - there is
    /// no socket to be open.
    /// </para>
    /// </remarks>
    private async Task<IngestResult> IngestAsync(bool deliverCommand, CancellationToken cancellationToken)
    {
        // Guaranteed present: the controller's [Authorize] only admits a request once
        // PrusaConnectPrinterAuthenticationHandler has resolved the Fingerprint header to a printer.
        int printerId = int.Parse(User.FindFirstValue(HSClaimTypes.PrinterId)!);

        JsonDocument document;

        try
        {
            // The same ceiling the websocket transport has always had, from the same setting - these
            // two carry the same messages, so a bound that differed between them would be an accident
            // rather than a decision. Without one the whole body is parsed into memory: authenticated,
            // so not a drive-by, but PrusaConnectOptions.MaxIncomingMessageBytes exists precisely
            // because one leaked printer credential should not be able to stop the server for
            // everyone.
            await using LengthLimitingStream bounded = new(Request.Body, _options.MaxIncomingMessageBytes);

            document = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken);
        }
        catch (UploadTooLargeException)
        {
            // Answered like any other unusable body. Firmware reads status codes here and treats every
            // 4xx alike, so introducing a 413 it has never been sent buys nothing.
            _logger.LogWarning("Printer {PrinterId} sent a body over the {Limit}-byte ceiling.",
                               printerId, _options.MaxIncomingMessageBytes);

            return TypedResults.BadRequest();
        }
        catch (JsonException e)
        {
            // A body that is not JSON is a protocol violation, and this codebase treats those as the
            // client's fault rather than an error of ours. On the socket the equivalent closes the
            // connection; here the request is simply refused and the printer retries.
            _logger.LogWarning(e, "Printer {PrinterId} posted a body that is not JSON.", printerId);

            return TypedResults.BadRequest();
        }

        // Touched before the body is looked at: reaching us at all is the liveness signal, and a
        // printer whose message we then refuse is still a printer that is there.
        // The user agent decides how a file may be offered to this printer later: firmware sends
        // none, the Python SDK sends its own. See HttpPrinterConnection.CanDecryptDownloads.
        IPrinterConnectionActor actor = _sessions.GetOrCreate(printerId, Request.Headers.UserAgent.ToString());

        using (document)
        {
            ConnectionMessage? message = _dispatcher.Classify(printerId, document.RootElement);

            if (message is InboundTransferRequestMessage)
            {
                // Inline transfers are a websocket mechanism - the printer asks for a chunk and the
                // answer is a frame on the same socket. There is nothing to answer with here, so
                // refuse it rather than accept a request that can never be served. Structurally it
                // could not be served anyway: HttpPrinterConnection is not an IChunkStreamingConnection.
                _logger.LogWarning(
                    "Printer {PrinterId} requested an inline transfer chunk over HTTP, which cannot be served.",
                    printerId);

                return TypedResults.BadRequest();
            }

            if (message is not null)
            {
                await actor.PostAsync(message, cancellationToken);
            }
        }

        if (!deliverCommand)
        {
            return TypedResults.NoContent();
        }

        // Ask the loop, and only the loop, whether a command is waiting: the parked slot is
        // loop-owned, and this request thread never reads it. Posted after the telemetry so that a
        // command answering something in this very message is not handed out ahead of it.
        TaskCompletionSource<PendingCommand?> take = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await actor.PostAsync(new TakePendingCommandMessage(take), cancellationToken);

        PendingCommand? pending = await take.Task.WaitAsync(cancellationToken);

        if (pending is null)
        {
            return TypedResults.NoContent();
        }

        CommandWireEncoder.Body body = CommandWireEncoder.EncodeBody(pending.Command);

        // Decimal, not the frame's hex: ExtractCommanId accumulates digit by digit as base ten
        // (command_id.cpp), so "0000002A" would parse as 2 and then choke on the A.
        Response.Headers[Headers.CommandId] = pending.CommandId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return TypedResults.File(body.Payload, body.ContentType);
    }
}
