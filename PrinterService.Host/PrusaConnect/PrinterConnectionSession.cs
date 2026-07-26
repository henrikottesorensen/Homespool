using System;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Everything a printer's WebSocket connection does after the upgrade has been accepted: register
/// its actor, run the read loop, then tear both down in order and close.
/// </summary>
/// <remarks>
/// Split out of <see cref="Controllers.PrusaConnectPrinterController.ConnectWebSocket"/> so the
/// teardown below can be tested at all. Welded to <c>HttpContext</c> it was reachable only by
/// standing up a real WebSocket upgrade, which is why none of the invariants its steps encode - each
/// one learned from a defect - had a test. The ordering here is not incidental; see the comments on
/// each step before moving one.
/// </remarks>
public sealed class PrinterConnectionSession
{
    private readonly WebSocketHandler _webSocketHandler;
    private readonly PrinterConnectionRegistry _connectionRegistry;
    private readonly PrinterConnectionActorFactory _actorFactory;
    private readonly ILogger<PrinterConnectionSession> _logger;

    /// <summary>
    /// How long teardown waits for the connection's actor to drain before abandoning it. Generous
    /// for a mailbox that only ever holds seconds of traffic, and finite because the actor loop's
    /// socket write is the one step no token cancels.
    /// </summary>
    /// <remarks>
    /// <b>Not configuration</b>, and deliberately not in <see cref="PrusaConnectOptions"/>: the only
    /// signal that would prompt anyone to change it is the abandonment warning below, whose real
    /// cause is a wedged printer or network rather than a wrong number here. Raising it would slow
    /// shutdown - the stall the caller's <c>ApplicationStopping</c> link exists to remove - and
    /// lowering it buys nothing, because abandoning is already the safe outcome. Settable only so a
    /// test needn't wait it out.
    /// </remarks>
    public TimeSpan ActorDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public PrinterConnectionSession(WebSocketHandler webSocketHandler,
                                    PrinterConnectionRegistry connectionRegistry,
                                    PrinterConnectionActorFactory actorFactory,
                                    ILogger<PrinterConnectionSession> logger)
    {
        _webSocketHandler = webSocketHandler;
        _connectionRegistry = connectionRegistry;
        _actorFactory = actorFactory;
        _logger = logger;
    }

    /// <summary>
    /// Runs the connection until the printer closes its end, the handler rejects what it sent, or
    /// <paramref name="cancellationToken"/> ends it - then tears down and closes. Returns normally
    /// for every ordinary end; only a protocol violation propagates.
    /// </summary>
    /// <param name="printerId">
    /// The printer this socket belongs to, already resolved from its fingerprint by the
    /// authentication handler. The registry key, and the correlation id on every log line here.
    /// </param>
    /// <param name="connection">
    /// The socket's write side, plus the close. Handed in rather than built here because only the
    /// caller holds the <see cref="System.Net.WebSockets.WebSocket"/> - and the caller keeps owning
    /// it, so this disposes nothing.
    /// </param>
    /// <param name="input">
    /// The socket's read side. <b>This method takes ownership of it and completes it</b> - in
    /// System.IO.Pipelines the consumer completes the reader, and the consumer is the handler run
    /// here. The caller keeps the stream and socket it was built over, which is what the reader's
    /// <c>leaveOpen: true</c> says.
    /// </param>
    /// <param name="cancellationToken">
    /// Ends the read loop for reasons that are not the printer's doing - the request being aborted,
    /// or the host shutting down. It is deliberately not threaded into the teardown below, which
    /// drains by completion instead; see the comments in the <c>finally</c>.
    /// </param>
    /// <exception cref="JsonException">The printer sent malformed JSON. Closed on, then rethrown.</exception>
    public async Task RunAsync(int printerId,
                               IClosablePrinterConnection connection,
                               PipeReader input,
                               CancellationToken cancellationToken)
    {
        IPrinterConnectionActor actor = _actorFactory.Create(printerId, connection);
        _connectionRegistry.Register(printerId, actor);

        // Which status the close frame carries. Recorded rather than sent inline, so every
        // exit closes in one place - after the connection has left the registry.
        //
        // NormalClosure covers both ways the handler returns without throwing: the printer
        // closed its end (EOF on the reader), and cancellation landing between reads rather
        // than inside one, where the loop condition ends it. The latter reaches here with the
        // socket still open, so shutdown sends a real close frame when it wins that race and
        // aborts the socket when it doesn't - both acceptable ends, and the printer
        // reconnects from either.
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;

        try
        {
            await _webSocketHandler.HandlePrusaWebsocket(input, printerId, actor, cancellationToken);
        }
        catch (JsonException)
        {
            // The handler's contract: malformed JSON is a protocol violation. Close on it,
            // then let it surface - same as when the handler owned the close itself.
            closeStatus = WebSocketCloseStatus.PolicyViolation;
            throw;
        }
        catch (WebSocketException e)
        {
            // Routine, not exceptional: printers drop off the network without a close
            // handshake all the time. The socket is already gone - nothing to close.
            _logger.LogDebug(e, "[{PrinterId}] connection closed abruptly", printerId);
        }
        catch (OperationCanceledException e)
        {
            // Shutdown or teardown, per the caller's linked token - including Kestrel aborting
            // the connection outright, since ConnectionAbortedException derives from this.
            // Ordinary ends to a WebSocket request, not faults: left unhandled they surfaced
            // as a logged 500.
            _logger.LogDebug(e, "[{PrinterId}] connection ended: shutting down or aborted", printerId);
        }
        finally
        {
            await input.CompleteAsync();

            // Unregister before closing, not after: while the connection is still in the
            // registry a command can pass its IsOpen check and start writing to this socket,
            // and the close frame is a write too. Removing it first bounds that race to
            // callers already holding a reference, which WebSocketPrinterConnection's write
            // lock then serializes against the close itself.
            _connectionRegistry.Unregister(printerId, actor);

            // Shutdown-by-completion, same as TelemetryWriter: complete the mailbox, let the
            // loop drain and exit. A command still awaiting its reply fails as NotConnected
            // in that drain - the actor's loop owns that, where this used to cancel the
            // correlator by hand.
            actor.Complete();

            // Bounded, unlike the drain itself. The loop's socket write is the one step
            // nothing cancels, so a send wedged against a stalled peer would hold this
            // request open with no ceiling at all - reintroducing the shutdown stall that the
            // caller's ApplicationStopping link exists to prevent, and without its 30s limit.
            // Abandoning the actor leaves that send to die with the request; the connection's
            // write lock is what keeps the close below from interleaving with it.
            try
            {
                await actor.Completion.WaitAsync(ActorDrainTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("[{PrinterId}] connection actor did not finish draining in time; abandoning it.", printerId);
            }
            catch (Exception e)
            {
                // Never rethrow from here: this is a finally, and would replace whatever
                // exception sent us into it.
                _logger.LogError(e, "[{PrinterId}] connection actor loop faulted.", printerId);
            }

            await connection.CloseOutputAsync(closeStatus);
        }
    }
}
