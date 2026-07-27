using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Write-only view of a printer's live connection. Command-sending code has no business touching
/// the receive side, <c>State</c> transitions, or the close handshake - only writing a frame.
/// Frames come from the printer's <see cref="PrinterConnectionActor"/> loop, and only from there.
/// </summary>
public interface IPrinterConnection
{
    bool IsOpen { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one transfer chunk as a single WebSocket <i>message</i>: <paramref name="header"/>
    /// followed by <paramref name="count"/> bytes pulled from <paramref name="content"/> starting at
    /// <paramref name="offset"/>, split across as many frames as the wire format requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not just repeated <see cref="SendAsync"/> calls.</b> A WebSocket frame's length
    /// is encoded in 7, 16 or 64 bits, and the firmware's client rejects the 64-bit form outright
    /// (<c>websocket.cpp:127-129</c> returns <c>Error::WebSocket</c>, which drops the connection).
    /// .NET emits exactly one frame per <see cref="SendAsync"/> call and never fragments on its own -
    /// measured - so a 256 KiB chunk sent in one call goes out with the 64-bit marker and kills the
    /// connection. Fragmenting is therefore mandatory, and it lives here because this class owns the
    /// socket and the write lock.
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
    /// <param name="cancellationToken">Cancels the reads. Never passed to the socket write, for the
    /// same reason <see cref="SendAsync"/> does not - a cancelled write leaves a partial frame on the
    /// wire.</param>
    ValueTask SendChunkAsync(ReadOnlyMemory<byte> header, ITransferContent content, long offset,
        long count, CancellationToken cancellationToken);
}
