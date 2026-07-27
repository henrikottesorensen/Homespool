using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The live WebSocket behind one printer's connection: serializes every frame written to it, and
/// owns the close handshake.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="IDisposable"/>, despite holding a <see cref="SemaphoreSlim"/>.
/// Disposing one only matters if its <c>AvailableWaitHandle</c> was used, which allocates an event -
/// this only ever calls <c>WaitAsync</c> and <c>Release</c>, so there is nothing to release. Making
/// it disposable would mean disposing at the end of the accepting request, where a command send from
/// another request can still be waiting on the lock: that turns a narrow teardown race into an
/// <see cref="ObjectDisposedException"/> thrown into somebody's API call, in exchange for freeing
/// nothing.
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
                 Justification = "The semaphore allocates no wait handle, and disposing it would race in-flight sends. See the remarks.")]
public sealed class WebSocketPrinterConnection : IClosablePrinterConnection
{
    /// <summary>
    /// The largest payload a single frame may carry. One below the 16-bit ceiling's successor: at
    /// 65 535 .NET writes the 16-bit length marker (126), and at 65 536 it switches to the 64-bit
    /// marker (127) - which the firmware's client rejects outright, dropping the connection
    /// (websocket.cpp:127-129). Measured, not inferred from the RFC.
    /// </summary>
    private const int MaxFramePayload = 65535;

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

    public async ValueTask SendChunkAsync(ReadOnlyMemory<byte> header, ITransferContent content,
        long offset, long count, CancellationToken cancellationToken)
    {
        // Held across every fragment. Two fragments of one message with somebody else's message in
        // between is not a message either party can parse - and the close frame is a send too.
        await _writeLock.WaitAsync(cancellationToken);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxFramePayload);

        // Returned only on the normal path. If a read is abandoned - a cancelled await whose
        // underlying syscall keeps going, which is the usual shape of file I/O on Unix - it may still
        // write into this buffer afterwards, and handing that to the next renter would corrupt an
        // unrelated caller's data. Dropping 64 KiB on an exceptional path is the cheaper mistake.
        bool completed = false;

        try
        {
            long remaining = count;

            // The header shares the first frame with as much payload as still fits, so a chunk that
            // fits in one frame stays one frame.
            int headerLength = header.Length;
            header.CopyTo(buffer);

            while (true)
            {
                int room = MaxFramePayload - headerLength;
                int want = (int)Math.Min(room, remaining);
                int filled = 0;

                while (filled < want)
                {
                    int read = await content
                        .ReadAsync(buffer.AsMemory(headerLength + filled, want - filled), offset + filled, cancellationToken);

                    if (read == 0)
                    {
                        // Short file. Nothing good can follow: under-delivering leaves the printer
                        // waiting forever, since the inline engine has no stall timeout at all.
                        // Better to fail loudly here than to hang a print silently.
                        throw new EndOfStreamException(
                            $"Transfer content ended at {offset + filled} with {remaining - filled} bytes still owed.");
                    }

                    filled += read;
                }

                offset += filled;
                remaining -= filled;

                bool last = remaining == 0;

                // The write keeps CancellationToken.None for the same reason SendAsync does:
                // cancelling mid-frame leaves a partial frame on the wire and aborts the socket.
                await _webSocket.SendAsync(buffer.AsMemory(0, headerLength + filled),
                    WebSocketMessageType.Binary, last, CancellationToken.None);

                if (last)
                {
                    completed = true;

                    return;
                }

                // Only the first frame carries the header; firmware parses it once per message and
                // reads the rest as payload (connect.cpp:445-532).
                headerLength = 0;
            }
        }
        finally
        {
            if (completed)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

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
