using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
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
/// what is known when this returns (<c>notes/backlog.md</c>: <i>"Accepted is treated as a command
/// completing, and for gcode it means queued"</i>). Claiming the filament is out would be claiming
/// something no event has yet said. A caller reporting this should say the unload has begun.
/// </para>
/// <para>
/// <b>Reachable from the web UI only</b>, on the same reasoning
/// <see cref="PrinterPreheatService"/> records at length: personal access tokens are unscoped, and
/// this is a physical consequence that persists after the holder stops paying attention. A signed-in
/// session is the authority of standing at the machine; a token is not.
/// </para>
/// </remarks>
public class PrinterFilamentService
{
    private readonly PrinterCommandService _commands;
    private readonly QueueSnapshotReader _snapshots;
    private readonly HomespoolDbContext _dbContext;

    public PrinterFilamentService(PrinterCommandService commands,
                                  QueueSnapshotReader snapshots,
                                  HomespoolDbContext dbContext)
    {
        _commands = commands;
        _snapshots = snapshots;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Unloads whatever the printer says is loaded.
    /// </summary>
    /// <param name="printerId">The printer to unload.</param>
    /// <param name="caller">Who is asking, checked for <c>CanUse</c> on the printer's team.</param>
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

        // Read from the live state rather than taken from the caller: the material is not decoration
        // here, it is the condition under which firmware will run this without a dialog on the panel.
        string? reported = await _dbContext.PrinterLiveStates
                                           .AsNoTracking()
                                           .Where(state => state.PrinterId == printerId)
                                           .Select(state => state.Material)
                                           .SingleOrDefaultAsync(cancellationToken);

        if (LoadedFilament.Of(reported) is not { } material)
        {
            throw new FilamentTypeUnknownException(printerId);
        }

        CommandOutcome? answer = await _commands.SendCommandAsync(
            printerId, new UnloadFilament(), caller, cancellationToken);

        // The printer's own answer decides, not the fact that a frame was written - the same rule
        // preheating follows, and for the same reason: a refusal shown as success sends somebody to
        // a printer that still has its filament in.
        if (answer is not null && answer.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            throw new PrinterRefusedException(answer.EventType, answer.Reason);
        }

        return new UnloadOutcome(material, answer);
    }
}

/// <summary>What was unloaded, and what the printer said about it.</summary>
/// <param name="Material">
/// The filament the printer named, e.g. <c>PLA</c>. Never the <c>"---"</c> sentinel - a printer
/// reporting that is refused before anything is sent.
/// </param>
/// <param name="Answer">
/// The printer's own answer, or null for a command expecting no reply. An <c>Accepted</c> here means
/// the unload has <em>started</em>, not that it has finished.
/// </param>
public sealed record UnloadOutcome(string Material, CommandOutcome? Answer);
