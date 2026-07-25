using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using PrinterService.Data;
using PrinterService.Host.Exceptions;
using PrinterService.Host.PrusaConnect.Commands;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Team-permission-checked entry point for sending a command to a printer - the first real
/// consumer of <see cref="TeamMember.CanUse"/>. Kept separate from
/// <see cref="PrinterConnectionActor"/>, which owns the wire send/correlation and has no database
/// access: permission checks stay off the actor's loop.
/// </summary>
public class PrinterCommandService
{
    private readonly PSDbContext _dbContext;
    private readonly TeamService _teamService;
    private readonly PrinterConnectionRegistry _registry;

    public PrinterCommandService(PSDbContext dbContext, TeamService teamService, PrinterConnectionRegistry registry)
    {
        _dbContext = dbContext;
        _teamService = teamService;
        _registry = registry;
    }

    /// <exception cref="PrinterNotFoundException" />
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c> on the printer's team.</exception>
    /// <exception cref="PrinterNotConnectedException" />
    /// <exception cref="CommandAlreadyInFlightException" />
    /// <exception cref="CommandTimedOutException" />
    /// <returns>The printer's actual answer - e.g. <c>Rejected</c>/"No print to pause" - not just
    /// whether the send succeeded.</returns>
    public async Task<CommandOutcome> SendCommandAsync(int printerId, ISendableCommand commandData, long userId, CancellationToken cancellationToken)
    {
        Printer? printer = await _dbContext.Printers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == printerId, cancellationToken);

        if (printer is null)
        {
            throw PrinterNotFoundException.ForId(printerId);
        }

        TeamMember? membership = await _teamService.GetMemberAsync(printer.TeamId, userId, cancellationToken);

        if (membership is null || !membership.CanUse)
        {
            throw new TeamAccessDeniedException();
        }

        if (!_registry.TryGet(printerId, out IPrinterConnectionActor? actor) || actor is null)
        {
            throw new PrinterNotConnectedException(printerId);
        }

        CommandSendResult result = await actor.SendCommandAsync(commandData, cancellationToken);

        return result.Outcome switch
        {
            CommandSendOutcome.NotConnected => throw new PrinterNotConnectedException(printerId),
            CommandSendOutcome.AlreadyInFlight => throw new CommandAlreadyInFlightException(printerId),
            CommandSendOutcome.TimedOut => throw new CommandTimedOutException(printerId),
            _ => result.Response!,
        };
    }
}
