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
    /// Payload bytes per WebSocket frame when the connection is encrypted. <b>Sized by the printer's TLS record buffer, not by the
    /// WebSocket protocol</b> - see the remarks, because the obvious value is wrong and the reason is
    /// three layers down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Was 65535</b>, which is the real WebSocket cap: their client implements only the 7-bit and
    /// 16-bit length encodings and rejects the 64-bit marker as <c>Error::WebSocket</c>, killing the
    /// connection (<c>websocket.cpp:127-129</c>). That is still true and still the ceiling. It is no
    /// longer the binding constraint.
    /// </para>
    /// <para>
    /// <b>The binding constraint is that the printer can hold 1024 bytes of TLS plaintext at a
    /// time.</b> Buddy builds mbedtls with <c>MBEDTLS_SSL_IN_CONTENT_LEN</c> 1024 and
    /// <c>OUT_CONTENT_LEN</c> 512 (<c>include/mbedtls/cipher_config_ece.h:69-74</c>), which reclaims
    /// ~30 KB of SRAM - 16% of an MK4's - and is why TLS fits on the board at all. It asks the server
    /// to respect that by negotiating RFC 6066 <c>max_fragment_length</c>, and Prusa's own servers
    /// honour it, which is how every Connect-connected printer transfers files today.
    /// </para>
    /// <para>
    /// <b><see cref="System.Net.Security.SslStream"/> does not.</b> Measured 2026-07-31 against a
    /// throwaway TLS 1.2 server: a client offering <c>max_fragment_length := 2^9 (512)</c> got a
    /// ServerHello carrying only <c>renegotiate</c> and <c>extended_master_secret</c> - no echo, so
    /// per RFC 6066 not honoured - and one 8 KB write produced a single 8216-byte record. Asked for
    /// in <c>dotnet/runtime#44241</c> since 2020, closed, still absent. RFC 8449
    /// <c>record_size_limit</c> would not help either: mbedtls 2.28 predates it.
    /// </para>
    /// <para>
    /// So the record size is ours to control, and the only lever is how much is handed to the stream
    /// per write: <see cref="System.Net.WebSockets.WebSocket.SendAsync(ReadOnlyMemory{byte}, System.Net.WebSockets.WebSocketMessageType, bool, CancellationToken)"/>
    /// is one frame is one write is one record. <b>1000 rather than 1024</b> leaves room for the
    /// frame header inside the record. Their reader handles the resulting continuation fragments
    /// properly - it consumes them until FIN and services Ping between them
    /// (<c>connect.cpp:369-553</c>) - and the header rides only the first, which the loop below
    /// already did.
    /// </para>
    /// <para>
    /// <b>What this cost, measured on an MK3.5:</b> nothing detectable. A 316 KB bgcode moved in
    /// ~1.2 s (~270 KB/s) against ~193 KB/s for a 1.9 MB file in 64 KiB frames over plaintext. That
    /// is not fast in any absolute sense - the inline engine is round-trip bound and
    /// <c>notes/transfer-protocol.md</c>, "Why throughput is capped", accounts for all of it - but
    /// ~1900 frames per file demonstrably do not make it worse.
    /// </para>
    /// <para>
    /// <b>One thing known and not addressed.</b> This assumes Kestrel writes one frame per record,
    /// which held on hardware but is behaviour rather than contract; if its output pipe ever batched
    /// two frames into one write the record would exceed 1024 and transfers would die again,
    /// intermittently - the worst shape. TLS record headers are unencrypted, so a capture can check
    /// that cheaply without decrypting anything, and that is the thing to do before trusting this
    /// under load.
    /// </para>
    /// </remarks>
    private const int TlsFramePayload = 1000;

    /// <summary>
    /// Payload bytes per frame in the clear, where the only limit is the WebSocket one their client
    /// can parse: at 65 535 .NET writes the 16-bit length marker (126), and at 65 536 it switches to
    /// the 64-bit marker (127), which the firmware rejects outright and drops the connection
    /// (<c>websocket.cpp:127-129</c>). Measured, not inferred from the RFC.
    /// </summary>
    private const int PlaintextFramePayload = 65535;

    private readonly WebSocket _webSocket;
    private readonly int _maxFramePayload;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Wraps an accepted socket, sized for the transport it arrived on.</summary>
    /// <param name="webSocket">The accepted socket, already upgraded.</param>
    /// <param name="overTls">
    /// Whether this connection is encrypted, which decides the frame size - see
    /// <see cref="TlsFramePayload"/> for why the two differ by 65x.
    /// </param>
    /// <remarks>
    /// Taken from the connection rather than from configuration on the same reasoning the listener
    /// split used for <c>Connection.LocalPort</c>: a flag can be set wrong, a socket cannot. It also
    /// means a deployment serving both stays correct on each.
    /// </remarks>
    public WebSocketPrinterConnection(WebSocket webSocket, bool overTls)
    {
        _webSocket = webSocket;
        _maxFramePayload = overTls ? TlsFramePayload : PlaintextFramePayload;
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

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_maxFramePayload);

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
                int room = _maxFramePayload - headerLength;
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
