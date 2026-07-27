using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>
/// The <c>data</c> object on the transfer events - <c>TRANSFER_INFO</c> and the terminal
/// <c>TRANSFER_FINISHED</c>/<c>TRANSFER_ABORTED</c>/<c>TRANSFER_STOPPED</c>. The second event
/// payload typed rather than left raw, for the same reason <see cref="InfoEventDataDTO"/> is:
/// something needs to read fields out of it.
/// </summary>
/// <remarks>
/// <para>
/// One class covers both shapes because they are the same object rendered by two branches of the
/// same code (render.cpp:513-543 at the pinned ref), and they overlap in exactly one field.
/// <c>TRANSFER_INFO</c> renders the full progress block; the terminal events render
/// <b>only</b> <see cref="StartCommandId"/>, and omit <c>data</c> altogether when even that is
/// absent. So every field is nullable and none may be assumed present.
/// </para>
/// <para>
/// <b><see cref="StartCommandId"/> is nested here, not at the event root</b> - which is easy to get
/// wrong, because <c>command_id</c> and <c>transfer_id</c> on the same events <i>are</i> at the
/// root. All three of firmware's emission sites put it inside <c>data</c>
/// (render.cpp:460, :530, :539-541). A root-level binding would deserialize to null forever without
/// ever failing.
/// </para>
/// </remarks>
public class TransferEventDataDTO
{
    /// <summary>
    /// The <c>command_id</c> of the <c>START_CONNECT_DOWNLOAD</c> that began this transfer
    /// (planner.cpp:486,769,811).
    /// </summary>
    /// <remarks>
    /// Not interchangeable with <see cref="EventDTO.CommandId"/>. That one means "this event answers
    /// that command" and is what <c>PrinterConnectionActor</c> correlates a pending send against;
    /// this means "this event concerns what that command started". The terminal transfer events are
    /// unsolicited and carry no <c>command_id</c> at all, so this is the only link back to the
    /// request that caused them.
    /// </remarks>
    [JsonPropertyName("start_cmd_id")]
    public uint? StartCommandId { get; set; }

    /// <summary>Total bytes the transfer expects, i.e. what we declared as <c>orig_size</c>.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("transferred")]
    public long? Transferred { get; set; }

    /// <summary>Percentage, not a fraction - firmware renders <c>progress_estimate() * 100</c> to
    /// one decimal (render.cpp:519).</summary>
    [JsonPropertyName("progress")]
    public double? Progress { get; set; }

    [JsonPropertyName("time_remaining")]
    public int? TimeRemaining { get; set; }

    [JsonPropertyName("time_transferring")]
    public int? TimeTransferring { get; set; }

    /// <summary>Absent until the destination is known - firmware guards this field on a non-null
    /// destination (render.cpp:524-529), so an upload with no path yet omits it rather than sending
    /// null.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// The transfer's direction and origin - <c>FROM_CONNECT</c> for one we initiated, or the
    /// literal <c>NO_TRANSFER</c> when <c>TRANSFER_INFO</c> is answered with nothing running, in
    /// which case it is the only field present.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
