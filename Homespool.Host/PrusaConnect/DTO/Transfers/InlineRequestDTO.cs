using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.Transfers;

/// <summary>
/// The printer asking for the next byte range of a Connect-initiated download
/// (<c>transfers::Download::InlineRequest</c>, rendered at render.cpp:100-119 of the pinned ref).
/// Recognised by its <c>"transfer": "inline"</c> marker, which is what keeps it out of the telemetry
/// branch - it carries neither <c>event</c> nor <c>state</c>.
/// </summary>
/// <remarks>
/// <para>
/// Requests are strictly sequential: firmware re-arms <c>inline_request()</c> only once the previous
/// range is fully delivered (download.cpp:526-554), so exactly one of these is outstanding at a time
/// per printer.
/// </para>
/// <para>
/// <b>The range must be answered in full.</b> The inline engine holds no timestamp of any kind
/// (<c>Download::Inline</c>, download.hpp:138-151) - <c>REQUEST_TIMEOUT</c> belongs to the unrelated
/// Async engine - so under-delivering leaves the printer waiting indefinitely rather than retrying.
/// Serve every byte, or fail deliberately with a zero-length chunk.
/// </para>
/// </remarks>
public class InlineRequestDTO
{
    /// <summary>
    /// A nonce <b>the printer</b> generated for this transfer (<c>rand_u()</c>, download.cpp:481-492)
    /// and expects echoed in the header of every chunk we send back. Firmware's own comment calls it
    /// "a safety feature - making sure we don't mix chunks of different file in ourselves": a chunk
    /// whose id does not match kills the transfer outright, with no retry.
    /// </summary>
    [JsonPropertyName("file_id")]
    public uint FileId { get; set; }

    /// <summary>First byte of the requested range, counted from the start of the file.</summary>
    [JsonPropertyName("start")]
    public long Start { get; set; }

    /// <summary><b>Inclusive</b> last byte - so the length is <c>End - Start + 1</c>, not
    /// <c>End - Start</c>. Firmware's own comment marks this ("End is inclusive",
    /// download.cpp:489).</summary>
    [JsonPropertyName("end")]
    public long End { get; set; }

    /// <summary>
    /// A filesystem-block alignment hint (firmware sends a constant 4096, render.cpp:112, commented
    /// "Relates both to size of the FS block"). <b>Not</b> the transfer unit and not a size we are
    /// asked to honour - the unit is <see cref="Start"/>..<see cref="End"/>. Modelled only so the
    /// field is accounted for rather than silently ignored.
    /// </summary>
    [JsonPropertyName("chunk")]
    public int? ChunkHint { get; set; }

    /// <summary>
    /// Our own token from <c>START_CONNECT_DOWNLOAD</c>, echoed back <b>only on the first request of
    /// a transfer</b> (render.cpp:104-108) - firmware sends the details block once and then omits it.
    /// So this is how a request is matched to the offer that provoked it; subsequent requests are
    /// matched on <see cref="FileId"/> instead.
    /// </summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>First request only. Echoed from the command we sent.</summary>
    [JsonPropertyName("team_id")]
    public ulong? TeamId { get; set; }

    /// <summary>
    /// First request only. The printer's own transfer id - the same value its <c>TRANSFER_INFO</c>
    /// and terminal transfer events carry at the root, and a 31-bit unsigned on the wire.
    /// </summary>
    [JsonPropertyName("transfer_id")]
    public int? TransferId { get; set; }
}
