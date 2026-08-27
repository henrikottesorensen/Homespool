namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Reboots the printer. <c>RESET_PRINTER</c> (also spelled <c>RESET</c>, command.cpp:179-180 at the
/// pinned ref), no arguments.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one command known to be unanswerable</b>, and the reason
/// <see cref="ISendableCommand.ExpectsReply"/> exists. <c>Planner::command</c>'s <c>ResetPrinter</c>
/// overload calls <c>printer.reset_printer()</c>, and the rejection built after it carries firmware's
/// own comment - "We reach this place only if the reset_printer fails to execute (can it?)"
/// (planner.cpp:960-966). A reset that works never replies, because there is nothing left running to
/// reply with.
/// </para>
/// <para>
/// Confirmed on the wire before this was written: a capture shows Prusa's Connect sending it nine
/// times over 57 seconds, unanswered, because the first one had already taken the printer down -
/// 76 seconds of silence, then a fresh <c>/p/ws</c> upgrade and <c>INFO</c>.
/// </para>
/// <para>
/// Sendable, but deliberately not reachable from any endpoint or page: rebooting someone's printer
/// mid-print is not a capability to expose without first deciding how it should be guarded.
/// </para>
/// </remarks>
public class ResetPrinter : ISendableCommand
{
    public string WireName => "RESET_PRINTER";

    /// <inheritdoc/>
    public bool ExpectsReply => false;
}
