using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Write-only view of a printer's live connection. Command-sending code has no business touching
/// the receive side, <c>State</c> transitions, or the close handshake - only writing a frame.
/// Frames come from the printer's <see cref="PrinterConnectionActor"/> loop, and only from there.
/// </summary>
public interface IPrinterConnection
{
    bool IsOpen { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}

/// <remarks>
/// Deliberately not <see cref="IDisposable"/>, despite holding a <see cref="SemaphoreSlim"/>.
/// Disposing one only matters if its <c>AvailableWaitHandle</c> was used, which allocates an event -
/// this only ever calls <c>WaitAsync</c> and <c>Release</c>, so there is nothing to release. Making
/// it disposable would mean disposing at the end of the accepting request, where a command send from
/// another request can still be waiting on the lock: that turns a narrow teardown race into an
/// <see cref="ObjectDisposedException"/> thrown into somebody's API call, in exchange for freeing
/// nothing.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "The semaphore allocates no wait handle, and disposing it would race in-flight sends. See the remarks.")]
public sealed class WebSocketPrinterConnection : IPrinterConnection
{
    private readonly WebSocket _webSocket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WebSocketPrinterConnection(WebSocket webSocket)
    {
        _webSocket = webSocket;
    }

    public bool IsOpen => _webSocket.State == WebSocketState.Open;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        // The lock survives the move to the actor, which serializes every command send onto one
        // loop and would otherwise make it redundant. Teardown is why: the close below is sent from
        // the request thread after a *bounded* wait on the actor's completion, so a send wedged
        // against a stalled peer can still be outstanding when the close goes out.
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            // Binary, endOfMessage per frame: the exact wire shape WebSocketPipe produced, which is
            // what the firmware has been accepting all along.
            await _webSocket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// How long the close frame waits for an in-flight command send. Long enough for any real send
    /// to finish, short enough that a send wedged against a stalled peer cannot hold teardown open.
    /// </summary>
    private static readonly TimeSpan CloseWriteLockTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sends the close frame if the socket can still take one. <c>CloseOutputAsync</c> rather than
    /// <c>CloseAsync</c>: it completes the handshake when the printer already sent its close frame,
    /// and when we initiate it doesn't wait for an ack a misbehaving peer may never send.
    /// </summary>
    /// <remarks>
    /// Takes <c>_writeLock</c>, because a close frame is a send: without it the one send that
    /// changes the socket's state was the one send the lock didn't cover, so teardown could put a
    /// close frame in the middle of a command a request thread was still writing. Giving up on the
    /// lock is deliberate - the close is a courtesy, and a peer that never gets it sees a dropped
    /// connection, which is what any abrupt disconnect looks like and which printers reconnect from.
    /// </remarks>
    public async Task CloseOutputAsync(WebSocketCloseStatus closeStatus)
    {
        if (!await _writeLock.WaitAsync(CloseWriteLockTimeout))
        {
            return;
        }

        try
        {
            // Checked under the lock: an in-flight send that faulted may have moved the socket out
            // of a closeable state while this was waiting.
            if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _webSocket.CloseOutputAsync(closeStatus, statusDescription: null, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // The peer vanished between the state check and the close frame - nothing to do.
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>
/// Maps a connected printer's id to its live <see cref="IPrinterConnectionActor"/>, so a command can
/// be sent from outside the request that accepted the WebSocket upgrade. A directory of actors,
/// nothing more: registered/unregistered by
/// <see cref="Controllers.PrusaConnectPrinterController.ConnectWebSocket"/> for the lifetime of that
/// request.
/// </summary>
public sealed class PrinterConnectionRegistry
{
    private readonly ConcurrentDictionary<int, IPrinterConnectionActor> _actors = new();

    public void Register(int printerId, IPrinterConnectionActor actor)
    {
        _actors[printerId] = actor;
    }

    /// <summary>
    /// Conditional (instance-matching) remove. A fast reconnect registers a new actor for the same
    /// <paramref name="printerId"/> before the old request's <c>finally</c> unregisters the old one;
    /// an unconditional remove would delete the new, live actor instead of the stale one. (The race
    /// is between two connection <i>lifetimes</i>, which the actor doesn't change - each connection
    /// still has a request that ends at its own pace.)
    /// </summary>
    public void Unregister(int printerId, IPrinterConnectionActor actor)
    {
        _actors.TryRemove(new KeyValuePair<int, IPrinterConnectionActor>(printerId, actor));
    }

    public bool TryGet(int printerId, out IPrinterConnectionActor? actor) => _actors.TryGetValue(printerId, out actor);

    public bool IsConnected(int printerId) => _actors.TryGetValue(printerId, out IPrinterConnectionActor? actor) && actor.IsOpen;
}
