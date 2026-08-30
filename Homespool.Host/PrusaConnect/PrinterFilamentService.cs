using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.Queue;
using Homespool.Model;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Unloading filament remotely, so a printer is ready for a new spool by the time somebody reaches
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The printer owns the temperature, and this composes nothing.</b> <c>M702 W0</c> heats to the
/// filament the printer itself has stored and cools everything down afterwards, so there is no
/// preset to look up here and no heat-and-wait to sequence - see <see cref="UnloadFilament"/> for
/// the firmware reading behind that. It also means the command comes back in milliseconds: gcode is
/// answered <c>Accepted</c> when it is <em>queued</em>, so the minutes the unload then takes are
/// firmware's business and never a response timeout here.
/// </para>
/// <para>
/// <b>Which is also why the answer says "started".</b> That same <c>Accepted</c> is the whole of
/// what is known when this returns - <c>Accepted</c> is treated as a command completing, and for
/// gcode it means queued. Claiming the filament is out would be claiming
/// something no event has yet said. A caller reporting this should say the unload has begun.
/// </para>
/// <para>
/// <b>Reachable from the web UI only</b>, on the same reasoning
/// <see cref="PrinterPreheatService"/> records at length: this is a physical consequence that
/// persists after the holder stops paying attention, so it stays off the API rather than behind a
/// token scope. A signed-in session is the authority of standing at the machine; a token is not.
/// </para>
/// </remarks>
public class PrinterFilamentService
{
    private readonly PrinterCommandService _commands;
    private readonly QueueSnapshotReader _snapshots;
    private readonly ToolTargetReader _tools;

    public PrinterFilamentService(PrinterCommandService commands,
                                  QueueSnapshotReader snapshots,
                                  ToolTargetReader tools)
    {
        _commands = commands;
        _snapshots = snapshots;
        _tools = tools;
    }

    /// <summary>
    /// Unloads whatever the printer says is loaded.
    /// </summary>
    /// <param name="printerId">The printer to unload.</param>
    /// <param name="caller">Who is asking, checked for <c>CanUse</c> on the printer's team.</param>
    /// <param name="toolNumber">
    /// Which tool to unload, <b>1-based as the printer numbers it</b>. Optional only on a single-tool
    /// printer; a toolchanger without one is <see cref="ToolNotSpecifiedException"/>, because there is
    /// no default that is not a guess at somebody's spool.
    /// </param>
    /// <param name="cancellationToken">The caller's own cancellation.</param>
    /// <returns>
    /// The material that was unloaded and the printer's own answer to the command. The material is
    /// read before sending rather than reported from the page's copy, so what is named in the
    /// confirmation is what the guard actually allowed.
    /// </returns>
    /// <exception cref="PrinterBusyException">The printer is mid-something.</exception>
    /// <exception cref="FilamentTypeUnknownException">The printer has not said what is loaded.</exception>
    /// <exception cref="PrinterHasQueuedWorkException">Ready, with work the loop could start.</exception>
    /// <exception cref="PrinterRefusedException">The printer declined the command.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c>.</exception>
    public async Task<UnloadOutcome> UnloadAsync(int printerId,
                                                 Caller caller,
                                                 int? toolNumber,
                                                 CancellationToken cancellationToken)
    {
        QueueSnapshot snapshot = await _snapshots.ReadAsync(printerId, cancellationToken);

        if (!PhysicalChangeRules.IsAllowed(snapshot.Status))
        {
            throw new PrinterBusyException(snapshot.Status);
        }

        // Unloading's own condition, on top of the shared rule. Ready means the loop may start the
        // head at any moment, and a print that starts with the filament out extrudes nothing and
        // says nothing - see PrinterHasQueuedWorkException for why preheating needs no equivalent.
        if (snapshot.Status == PrinterStatus.Ready && snapshot.Head is not null)
        {
            throw new PrinterHasQueuedWorkException(printerId);
        }

        IReadOnlyList<PrinterToolState> tools = await _tools.ReadToolsAsync(printerId, cancellationToken);
        PrinterToolState tool = Resolve(printerId, tools, toolNumber);

        if (tool.Material is not { } material)
        {
            throw new FilamentTypeUnknownException(printerId);
        }

        CommandOutcome? answer = await _commands.SendCommandAsync(
            printerId, UnloadFilament.ForTool(tool.ToolNumber), caller, cancellationToken);

        // The printer's own answer decides, not the fact that a frame was written - the same rule
        // preheating follows, and for the same reason: a refusal shown as success sends somebody to
        // a printer that still has its filament in.
        if (answer is not null && answer.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            throw new PrinterRefusedException(answer.EventType, answer.Reason);
        }

        return new UnloadOutcome(material, tool.ToolNumber, tools.Count > 1, answer);
    }

    /// <summary>
    /// Which tool to act on, from what the caller asked for and what the printer reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A named tool is checked against the printer's own list, never trusted.</b> The number
    /// arrives in a form post, and an unrecognised one must not become a <c>T</c> argument: firmware
    /// would refuse a disabled tool, but a number that happens to name a <em>different</em> fitted
    /// head would unload the wrong spool, which nothing downstream could catch.
    /// </para>
    /// <para>
    /// <b>Omitting it is only meaningful on a single-tool printer.</b> On a toolchanger there is a
    /// real choice and no defensible default - picking the first fitted head, or whichever is on the
    /// carriage, would be guessing at somebody's spool. The dialog exists to make that choice, so a
    /// post without one is a defect rather than a shorthand.
    /// </para>
    /// </remarks>
    private static PrinterToolState Resolve(int printerId,
                                            IReadOnlyList<PrinterToolState> tools,
                                            int? toolNumber)
    {
        if (tools.Count == 0)
        {
            throw new FilamentTypeUnknownException(printerId);
        }

        if (toolNumber is not { } requested)
        {
            return tools.Count == 1 ? tools[0] : throw new ToolNotSpecifiedException(printerId);
        }

        return tools.SingleOrDefault(candidate => candidate.ToolNumber == requested)
               ?? throw new NoSuchToolException(printerId, requested);
    }
}

/// <summary>What was unloaded, and what the printer said about it.</summary>
/// <param name="Material">
/// The filament the printer named, e.g. <c>PLA</c>. Never the <c>"---"</c> sentinel - a printer
/// reporting that is refused before anything is sent.
/// </param>
/// <param name="Tool">
/// The tool it acted on, <b>1-based as the wire numbers it</b>. Always set - every unload now names
/// its tool, because <c>M702</c> carries an explicit <c>T</c>.
/// </param>
/// <param name="NamedByTool">
/// Whether saying <em>which</em> tool would mean anything to a reader. False on a single-tool
/// printer, where "tool 1" is noise rather than information.
/// </param>
/// <param name="Answer">
/// The printer's own answer, or null for a command expecting no reply. An <c>Accepted</c> here means
/// the unload has <em>started</em>, not that it has finished.
/// </param>
public sealed record UnloadOutcome(string Material, int Tool, bool NamedByTool, CommandOutcome? Answer);
