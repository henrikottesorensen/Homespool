using System.Text.Json.Serialization;

namespace Homespool.Host.PrusaConnect.DTO.EventMessages;

/// <summary>
/// The <c>data</c> object on a <c>JOB_INFO</c> event - what a <c>SEND_JOB_INFO</c> comes back with,
/// and the only thing on the wire that names <i>which file</i> a printer is printing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Telemetry cannot answer this and never could.</b> It carries <c>job_id</c> and a status, so it
/// can say a printer is printing something; it has no field naming the file. That is the whole
/// reason this type exists: adopting a running print as ours is a decision that deletes somebody's
/// queue entry, and it has to rest on identity rather than on coincidence
/// (<c>notes/print-queue.md</c>, "A timeout is not a negative answer").
/// </para>
/// <para>
/// <b>Only the current job renders a name.</b> Firmware's own fixtures have four shapes for this
/// answer: the running job renders <c>state</c>/<c>display_name</c>/<c>path</c>; a job it merely
/// remembers renders <c>state</c> alone as <c>FIN_OK</c> or <c>FIN_STOPPED</c>; a job it has never
/// heard of is a <c>REJECTED</c> with <c>"Job ID doesn't match"</c>; and no job at all is a
/// <c>REJECTED</c> with <c>"No job in progress"</c>. So a null <see cref="Path"/> is a real answer -
/// "there was such a job and it is over" - not a parse failure, and it must not be read as a match.
/// </para>
/// <para>
/// <b><see cref="State"/> is the <i>job</i>'s state, not the printer's</b>, and its vocabulary is a
/// different one - <c>FIN_OK</c> and <c>FIN_STOPPED</c> appear nowhere in
/// <see cref="Homespool.Model.PrinterStatus"/>. Kept as the string firmware sent for the reason
/// <see cref="FileInfoEventDataDTO.Type"/> is: a value we have not seen should degrade to "something
/// else" rather than throw while parsing an answer that is otherwise perfectly readable.
/// </para>
/// <para>
/// The event's own <c>job_id</c> sits at the <i>root</i> rather than in here, so it is not modelled:
/// the caller already knows which id it asked about, and correlation is by <c>command_id</c>.
/// </para>
/// </remarks>
public class JobInfoEventDataDTO
{
    /// <summary>
    /// The printer's 8.3 alias for the file being printed, e.g. <c>/usb/SHAPE-~1.BGC</c>. Null once
    /// the job is over.
    /// </summary>
    /// <remarks>
    /// The same alias <c>FILE_INFO</c> reports, which is what <c>PrintFileOnPrinter.PrinterPath</c>
    /// stores and what <c>START_PRINT</c> was sent - so this is directly comparable to what we asked
    /// for, with no name conversion of our own in between.
    /// </remarks>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>The long name, as a person wrote it. Null once the job is over.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The job's own state - <c>PRINTING</c> while it runs, <c>FIN_OK</c> or <c>FIN_STOPPED</c> for
    /// one the printer only remembers.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>Bytes, when firmware had the file's stat to hand.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>Last-modified, as a Unix timestamp in seconds.</summary>
    [JsonPropertyName("m_timestamp")]
    public long? ModifiedTimestamp { get; set; }
}
