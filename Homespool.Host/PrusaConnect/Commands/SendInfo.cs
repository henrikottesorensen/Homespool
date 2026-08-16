using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.DTO.EventMessages;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Asks the printer to describe itself: firmware, serial, nozzle, tools, network - and the storage
/// block, which is the only place free space is reported.
/// </summary>
/// <remarks>
/// <para>
/// <b>Typed since 2026-08-03, because the queue needs the answer rather than the verdict.</b> It was
/// a bare <see cref="ICommand"/> marker, which meant a caller could learn that the printer replied
/// and not what it said. Send it with <see cref="PrinterCommandService.AskAsync"/>, the same route
/// <see cref="SendFileInfo"/> takes.
/// </para>
/// <para>
/// <b>Asking is the only way to know free space, and it has to be asked each time.</b> An unsolicited
/// <c>INFO</c> arrives on connect and whenever <c>info_fingerprint()</c> changes - and free space is
/// not part of that fingerprint, so a figure kept from connect time goes stale precisely as a queue
/// fills the drive, which is the one case it would ever be consulted for.
/// </para>
/// <para>
/// Firmware answers with an <c>INFO</c> event carrying the same <c>command_id</c>
/// (<c>planner.cpp:735-740</c>), so it correlates like any other command.
/// </para>
/// </remarks>
public class SendInfo : ISendableCommand<InfoEventDataDTO>
{
    public string WireName => "SEND_INFO";
}
