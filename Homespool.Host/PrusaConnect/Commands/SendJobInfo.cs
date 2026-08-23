using System.Collections.Generic;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Asks the printer to describe a job by id - and so what file it is printing, which nothing else on
/// the wire will say.
/// </summary>
/// <remarks>
/// <para>
/// <b>Typed since 2026-08-22, because the queue needs to know whether a print is ours.</b> It was a
/// hollow marker like most of this folder. What made it real is that a <c>START_PRINT</c> can time
/// out on a printer that accepted it, leaving a print running that Homespool has no record of: the
/// only honest way to adopt that print is to ask which file it is, and telemetry carries a
/// <c>job_id</c> but no name. Send it with <see cref="PrinterCommandService.AskAsync"/>, the route
/// <see cref="SendFileInfo"/> and <see cref="SendInfo"/> take.
/// </para>
/// <para>
/// <b>Four answers, and three of them are not a <c>JOB_INFO</c></b> - firmware's own render fixtures
/// have all four. The running job answers <c>JOB_INFO</c> with a path and a display name; a job the
/// printer merely remembers answers <c>JOB_INFO</c> with nothing but a <c>FIN_OK</c>/<c>FIN_STOPPED</c>
/// state; an id it does not recognise is <c>Rejected</c> <c>"Job ID doesn't match"</c>; and a printer
/// with no job at all is <c>Rejected</c> <c>"No job in progress"</c>. That last one is the only
/// <i>definite</i> negative any of this has, and it is why the queue asks rather than inferring from
/// a status. See <see cref="JobInfoEventDataDTO"/>.
/// </para>
/// <para>
/// Connect sends this in production - 13 times across the captures, always with a <c>job_id</c>
/// kwarg - and hardware answered one in 0.13 s (<c>notes/protocol-reference.md</c>).
/// </para>
/// </remarks>
public class SendJobInfo : ISendableCommand<JobInfoEventDataDTO>
{
    /// <summary>
    /// Which job to describe. <b>Firmware will not answer without one</b>, so this is read from the
    /// <c>job_id</c> telemetry reports rather than remembered.
    /// </summary>
    /// <remarks>
    /// <c>int</c> to match every other job id in this codebase - <c>PrinterLiveState.JobId</c>,
    /// <c>PrinterEvent.JobId</c>, <c>PrintJob.FirmwareJobId</c> - since the value is only ever copied
    /// from one of them. It was a <c>ushort</c> while nothing sent it, which would have needed a
    /// narrowing conversion at the one call site that now does.
    /// </remarks>
    public int JobId { get; set; }

    public string WireName => "SEND_JOB_INFO";

    /// <summary>The one kwarg, and the only reason this command is not <c>NO_ARGS</c>.</summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["job_id"] = JobId,
    };

    /// <inheritdoc />
    /// <remarks>It asks a question and changes nothing.</remarks>
    public Capability RequiredCapability => Capability.ViewPrinter;
}
