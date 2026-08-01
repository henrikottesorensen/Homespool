using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Team-permission-checked entry point for sending a command to a printer - the first real
/// consumer of <see cref="TeamMember.CanUse"/>. Kept separate from
/// <see cref="PrinterConnectionActor"/>, which owns the wire send/correlation and has no database
/// access: permission checks stay off the actor's loop.
/// </summary>
public class PrinterCommandService
{
    private readonly HSDbContext _dbContext;
    private readonly TeamService _teamService;
    private readonly PrinterConnectionRegistry _registry;

    public PrinterCommandService(HSDbContext dbContext, TeamService teamService, PrinterConnectionRegistry registry)
    {
        _dbContext = dbContext;
        _teamService = teamService;
        _registry = registry;
    }

    /// <summary>
    /// Sends <paramref name="commandData"/> to a printer <paramref name="userId"/> is allowed to use,
    /// and waits for the printer's own reply. Every way this can fail throws - the return value is
    /// only ever a real answer from the hardware.
    /// </summary>
    /// <exception cref="PrinterNotFoundException">No printer has id <paramref name="printerId"/>.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <c>CanUse</c> on the printer's team.</exception>
    /// <exception cref="PrinterNotConnectedException">
    /// The printer has no live WebSocket - either absent from the registry when the send was
    /// attempted, or its connection torn down while the command was in the actor's mailbox.
    /// </exception>
    /// <exception cref="CommandAlreadyInFlightException">
    /// Another command is still awaiting its reply. One in flight per printer is deliberate: replies
    /// are correlated by <c>command_id</c>, and the firmware answers them one at a time.
    /// </exception>
    /// <exception cref="CommandResponseTimedOutException">
    /// The printer never answered within <c>PrusaConnectOptions.CommandResponseTimeout</c>. It says
    /// nothing about whether the command was acted on - the frame was written to the socket.
    /// </exception>
    /// <returns>
    /// The printer's actual answer - e.g. <c>Rejected</c>/"No print to pause" - not just whether the
    /// send succeeded. <b>Null</b> for a command declaring
    /// <see cref="ISendableCommand.ExpectsReply"/> false, where the frame was written and no answer
    /// will ever come: that is success, not a shortfall, and callers must not read it as failure. See
    /// <see cref="CommandSendOutcome.Dispatched"/>.
    /// </returns>
    public async Task<CommandOutcome?> SendCommandAsync(int printerId, ISendableCommand commandData, long userId, CancellationToken cancellationToken)
    {
        CommandSendResult result = await SendAndCheckAsync(printerId, commandData, userId, cancellationToken);

        // Written, and nothing will answer it. Null rather than an invented event: there is no
        // outcome to report, and fabricating one would misrepresent the wire.
        return result.Outcome == CommandSendOutcome.Dispatched ? null : result.Response!;
    }

    /// <summary>
    /// Asks a printer a question and hands back the answer already parsed into
    /// <typeparamref name="TAnswer"/> - the counterpart to <see cref="SendCommandAsync"/>, for the
    /// commands whose answer is a payload rather than a verdict.
    /// </summary>
    /// <typeparam name="TAnswer">Declared by the command itself, via <see cref="ISendableCommand{TAnswer}"/>.</typeparam>
    /// <remarks>
    /// <para>
    /// <b>A separate name rather than an overload, deliberately.</b> Two overloads separated only by
    /// <c>ISendableCommand</c> versus <c>ISendableCommand&lt;TAnswer&gt;</c> resolve on argument type,
    /// and the failure mode of picking the wrong one is silent: the payload is simply dropped and the
    /// caller sees a verdict with no answer, which looks exactly like an empty listing. A distinct
    /// name makes that unrepresentable. It also reads as what it is - a command is <i>sent</i>, a
    /// question is <i>asked</i>.
    /// </para>
    /// <para>
    /// This is the one place an answering event's <c>data</c> is deserialised, and the reason
    /// <c>CommandSendResult.Data</c> goes no further.
    /// </para>
    /// </remarks>
    /// <exception cref="CommandAnswerUnreadableException">
    /// The printer answered with a payload that will not parse as <typeparamref name="TAnswer"/>.
    /// </exception>
    /// <returns>
    /// The answer, with <see cref="CommandOutcome{TAnswer}.Answer"/> null when the printer replied
    /// without a payload - a <c>Rejected</c> being the ordinary case, where
    /// <see cref="CommandOutcome{TAnswer}.Reason"/> is what the caller wants. Null overall only for
    /// a command declaring <see cref="ISendableCommand.ExpectsReply"/> false, as
    /// <see cref="SendCommandAsync"/>.
    /// </returns>
    public async Task<CommandOutcome<TAnswer>?> AskAsync<TAnswer>(int printerId, ISendableCommand<TAnswer> commandData,
        long userId, CancellationToken cancellationToken)
    {
        CommandSendResult result = await SendAndCheckAsync(printerId, commandData, userId, cancellationToken);

        if (result.Outcome == CommandSendOutcome.Dispatched)
        {
            return null;
        }

        TAnswer? answer;

        try
        {
            answer = result.Data is { } data ? data.Deserialize<TAnswer>() : default;
        }
        catch (JsonException e)
        {
            // Thrown rather than returned as a null answer, which would be indistinguishable from the
            // printer refusing the command - and this service's contract is that everything that can
            // go wrong throws, so the return value is only ever a real answer from the hardware.
            throw new CommandAnswerUnreadableException(printerId, commandData.WireName, e);
        }

        return new CommandOutcome<TAnswer>(result.Response!.EventType, result.Response.Reason, answer);
    }

    /// <summary>
    /// The half both entry points share: check the caller may use this printer, send, and turn every
    /// way the send fell short into the exception that describes it. What comes back is always a
    /// <see cref="CommandSendOutcome.Completed"/> or <see cref="CommandSendOutcome.Dispatched"/>
    /// result, which is the only distinction the two callers still have to make.
    /// </summary>
    private async Task<CommandSendResult> SendAndCheckAsync(int printerId, ISendableCommand commandData, long userId,
        CancellationToken cancellationToken)
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
            CommandSendOutcome.ResponseTimedOut => throw new CommandResponseTimedOutException(printerId),
            CommandSendOutcome.SendTimedOut => throw new CommandSendTimedOutException(printerId),
            _ => result,
        };
    }
}
