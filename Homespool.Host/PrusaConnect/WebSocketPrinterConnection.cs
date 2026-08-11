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
                 Justification =
                     "The semaphore allocates no wait handle, and disposing it would race in-flight sends. See the remarks.")]
public sealed class WebSocketPrinterConnection : IClosablePrinterConnection
{
    /// <summary>
    /// Payload bytes per WebSocket frame. The only limit is the WebSocket one their client can parse:
    /// at 65 535 .NET writes the 16-bit length marker (126), and at 65 536 it switches to the 64-bit
    /// marker (127), which the firmware rejects outright and drops the connection
    /// (<c>websocket.cpp:127-129</c>). Measured, not inferred from the RFC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was 1000 on TLS connections for one day, and the reason it is not any more is the
    /// whole of why nginx now terminates printer TLS.</b> The problem was real: Buddy builds mbedtls
    /// with <c>MBEDTLS_SSL_IN_CONTENT_LEN</c> 1024 (<c>cipher_config_ece.h:69-74</c>), reclaiming
    /// ~30 KB of SRAM and making TLS fit on the board at all, so a printer holds one kilobyte of TLS
    /// plaintext at a time and a 256 KiB chunk write is far past it. It asks the server to respect
    /// that by negotiating RFC 6066 <c>max_fragment_length</c>, and Prusa's own servers honour it -
    /// which is how every Connect-connected printer transfers files today.
    /// </para>
    /// <para>
    /// <b><see cref="System.Net.Security.SslStream"/> does not honour it</b> (measured 2026-07-31: a
    /// client offering <c>2^9</c> got no echo in the ServerHello and one 8 KB write produced a single
    /// 8216-byte record; <c>dotnet/runtime#44241</c>, open since 2020, closed unimplemented). So the
    /// fix that shipped drove record size from this end instead, one frame per write per record, and
    /// it worked on an MK3.5 - but only because Kestrel happened not to batch two frames into one
    /// write. That was behaviour rather than contract, and unprovable by testing: an observation only
    /// shows batching did not happen while it was watched.
    /// </para>
    /// <para>
    /// <b>OpenSSL honours the extension, so the guarantee now lives at the layer that negotiated
    /// it</b> - 536-byte records against a client asking for 2^9, measured the same day. nginx is
    /// OpenSSL and terminates the printer connection, which leaves nothing here to guess at: this
    /// process writes plain HTTP to a proxy on the same host, and record size is not its business.
    /// See <c>notes/tls-by-default.md</c>, "Decision 3a's premise has shifted".
    /// </para>
    /// <para>
    /// <b>If a transfer over TLS fails again, this is the thing to suspect first and NOT the thing to
    /// fix.</b> A large frame failing means nginx did not honour <c>max_fragment_length</c> after
    /// all - check the record sizes in a capture, which needs no decryption since record headers are
    /// in the clear. Putting 1000 back would mask that, and mask it in the specific way that made the
    /// original bug invisible for a day: it would work, and prove nothing about why.
    /// </para>
    /// </remarks>
    private const int MaxFramePayload = 65535;

    private readonly WebSocket _webSocket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Wraps an accepted socket.</summary>
    /// <param name="webSocket">The accepted socket, already upgraded.</param>
    /// <remarks>
    /// It used to take the transport as well, to size frames for it. It no longer needs to know: the
    /// socket is plain HTTP to the proxy however a printer connected, and the only party that has to
    /// care about TLS record sizes is the one holding the TLS session. See
    /// <see cref="MaxFramePayload"/>.
    /// </remarks>
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

    public async ValueTask SendChunkAsync(ReadOnlyMemory<byte> header,
                                          ITransferContent content,
                                          long offset,
                                          long count,
                                          CancellationToken cancellationToken)
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
