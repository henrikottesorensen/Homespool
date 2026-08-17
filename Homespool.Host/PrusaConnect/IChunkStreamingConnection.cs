using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// A connection that can carry the inline transfer engine: chunks pulled by the printer over the
/// same channel its commands arrive on. Only a socket is one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate interface rather than a method that throws.</b> The pre-websocket HTTP
/// transport has no such channel - firmware's own URL for an inline request in that mode is an
/// <c>assert(0)</c> annotated <i>"Not used in non-websocket mode"</i> (connect.cpp:115-119 at
/// v6.2.6). An HTTP connection that implemented this by throwing would look complete and be a
/// print-killer at runtime; one that does not implement it cannot be asked, and a caller has to
/// type-test for the capability, which is the check that makes it choose
/// <c>START_ENCRYPTED_DOWNLOAD</c> instead.
/// </para>
/// <para>
/// Both members here are transfer-only raw-frame writes, which is why the empty-chunk failure
/// signal is here too and not on <see cref="IPrinterConnection"/>: nothing but the transfer engine
/// may ever send an empty chunk (<see cref="PrinterConnectionActor"/> documents why), and the type
/// now says so.
/// </para>
/// </remarks>
public interface IChunkStreamingConnection : IPrinterConnection
{
    /// <summary>
    /// Sends one transfer chunk as a single WebSocket <i>message</i>: <paramref name="header"/>
    /// followed by <paramref name="count"/> bytes pulled from <paramref name="content"/> starting at
    /// <paramref name="offset"/>, split across as many frames as the wire format requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not just repeated frame writes.</b> A WebSocket frame's length is encoded in
    /// 7, 16 or 64 bits, and the firmware's client rejects the 64-bit form outright
    /// (<c>websocket.cpp:127-129</c> returns <c>Error::WebSocket</c>, which drops the connection).
    /// .NET emits exactly one frame per send call and never fragments on its own - measured - so a
    /// 256 KiB chunk sent in one call goes out with the 64-bit marker and kills the connection.
    /// Fragmenting is therefore mandatory, and it lives here because this class owns the socket and
    /// the write lock.
    /// </para>
    /// <para>
    /// The whole message is written under a single acquisition of that lock. Fragments of one message
    /// cannot be interleaved with another message on the same connection, so a command send or the
    /// close frame slipping between two fragments would corrupt the stream for both.
    /// </para>
    /// <para>
    /// Reads are interleaved with the sends rather than buffered up front, so no 256 KiB buffer ever
    /// exists - only one frame's worth at a time.
    /// </para>
    /// </remarks>
    /// <param name="header">The 9-byte <c>'T'</c> frame header, sent once at the start of the
    /// message.</param>
    /// <param name="content">Where the bytes come from.</param>
    /// <param name="offset">Offset into <paramref name="content"/> of the first byte to send.</param>
    /// <param name="count">How many bytes to send. Must be delivered in full: firmware has no stall
    /// timeout on this path, so a short message leaves the printer waiting forever.</param>
    /// <param name="cancellationToken">Cancels the reads. Never passed to the socket write - a
    /// cancelled write leaves a partial frame on the wire.</param>
    ValueTask SendChunkAsync(ReadOnlyMemory<byte> header,
                             ITransferContent content,
                             long offset,
                             long count,
                             CancellationToken cancellationToken);

    /// <summary>
    /// Sends the header alone - a zero-length chunk, firmware's defined "error indicated by server"
    /// signal (download.cpp:556-577) and the one way to end a transfer promptly.
    /// </summary>
    /// <param name="header">The 9-byte <c>'T'</c> frame header for the transfer being ended.</param>
    /// <param name="cancellationToken">Cancels a wait for the write lock.</param>
    ValueTask SendEmptyChunkAsync(ReadOnlyMemory<byte> header, CancellationToken cancellationToken);
}
