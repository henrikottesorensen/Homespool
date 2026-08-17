using System.Text;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// Builds the 9-byte header that precedes a transfer chunk's payload - type byte <c>'T'</c> plus the
/// id as eight uppercase hex digits, the same header shape every server-to-printer message uses
/// (connect.cpp:363-366, :515-517 at the pinned ref).
/// </summary>
/// <remarks>
/// Separate from <see cref="Commands.CommandWireEncoder"/> despite the identical layout, because the
/// id means the opposite thing. A command's id is one <b>we</b> allocate and the printer echoes back
/// to correlate its reply. A chunk's id is the <c>file_id</c> <b>the printer</b> generated
/// (<c>rand_u()</c>, download.cpp:481-492) and we echo back: get it wrong and firmware treats the
/// chunk as belonging to a different file and kills the transfer, with no retry
/// (download.cpp:556-577).
/// <para>
/// Header only - the payload is streamed after it rather than concatenated, so that a 256 KiB chunk
/// never has to exist as a single buffer. See
/// <see cref="IChunkStreamingConnection.SendChunkAsync"/>.
/// </para>
/// </remarks>
public static class ChunkWireEncoder
{
    /// <summary>Type byte, 8 hex digits.</summary>
    public const int HeaderLength = 9;

    /// <summary>
    /// Writes the header for a chunk belonging to <paramref name="fileId"/>.
    /// </summary>
    /// <param name="fileId">The printer's own nonce for this transfer, echoed verbatim.</param>
    public static byte[] EncodeHeader(uint fileId)
    {
        byte[] header = new byte[HeaderLength];

        header[0] = (byte)'T';
        Encoding.ASCII.GetBytes(fileId.ToString("X8"), 0, 8, header, 1);

        return header;
    }
}
