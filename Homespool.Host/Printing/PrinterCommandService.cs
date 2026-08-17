using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Permission-checked entry point for sending a command to a printer. Kept separate from
/// <see cref="PrinterConnectionActor"/>, which owns the wire send/correlation and has no database
/// access: permission checks stay off the actor's loop.
/// </summary>
/// <remarks>
/// <b>It asks <see cref="PrinterAccessService"/> rather than reading the membership itself</b>
/// (2026-08-03). It used to be described as the first, and then the one, consumer of
/// <c>TeamMember.CanUse</c> - which had stopped being true: five other places were resolving a
/// printer and checking a flag on its team by then, in three different refusal shapes. The check is
/// unchanged; only its address is.
/// </remarks>
public class PrinterCommandService
{
    private readonly PrinterAccessService _access;
    private readonly PrinterConnectionRegistry _registry;

    public PrinterCommandService(PrinterAccessService access, PrinterConnectionRegistry registry)
    {
        _access = access;
        _registry = registry;
    }

    /// <summary>Turns every way a send fell short into the exception that describes it.</summary>
    private static CommandSendResult Check(int printerId, CommandSendResult result)
    {
        return result.Outcome switch
        {
            CommandSendOutcome.NotConnected => throw new PrinterNotConnectedException(printerId),
            CommandSendOutcome.AlreadyInFlight => throw new CommandAlreadyInFlightException(printerId),
            CommandSendOutcome.ResponseTimedOut => throw new CommandResponseTimedOutException(printerId),
            CommandSendOutcome.SendTimedOut => throw new CommandSendTimedOutException(printerId),
            _ => result,
        };
    }

    /// <summary>
    /// Sends <paramref name="commandData"/> to a printer the caller is allowed to use,
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
    public async Task<CommandOutcome?> SendCommandAsync(int printerId,
                                                        ISendableCommand commandData,
                                                        Caller caller,
                                                        CancellationToken cancellationToken)
    {
        CommandSendResult result = await SendAndCheckAsync(printerId, commandData, caller, cancellationToken);

        // Written, and nothing will answer it. Null rather than an invented event: there is no
        // outcome to report, and fabricating one would misrepresent the wire.
        return result.Outcome == CommandSendOutcome.Dispatched ? null : result.Response!;
    }

    /// <summary>
    /// Sends a domain intent - the protocol-free entry point, and what callers outside the wire
    /// layer use. Goes through <see cref="IPrinterLink"/>, so the connection translates the intent
    /// into whatever protocol it speaks and this service never learns which. Identical contract to
    /// the <see cref="ISendableCommand"/> overload, which remains for the callers that are
    /// legitimately wire-typed.
    /// </summary>
    /// <param name="printerId">The printer to send to.</param>
    /// <param name="intent">What the caller wants done, translated by the printer's own link.</param>
    /// <param name="caller">Who is asking, checked for the intent's own capability on the printer's team and against the credential's scope.</param>
    /// <param name="cancellationToken">The caller's own cancellation.</param>
    public async Task<CommandOutcome?> SendCommandAsync(int printerId,
                                                        IPrinterIntent intent,
                                                        Caller caller,
                                                        CancellationToken cancellationToken)
    {
        IPrinterLink link = await RequireLinkAsync(printerId, caller, intent.RequiredCapability, cancellationToken);
        CommandSendResult result = Check(printerId, await link.SendAsync(intent, cancellationToken));

        return result.Outcome == CommandSendOutcome.Dispatched ? null : result.Response!;
    }

    /// <summary>
    /// Asks a printer a question and hands back the answer already parsed into
    /// <typeparamref name="TAnswer"/> - the counterpart to <see cref="SendCommandAsync(int, ISendableCommand, Homespool.Model.Caller, System.Threading.CancellationToken)"/>, for the
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
    /// <see cref="SendCommandAsync(int, ISendableCommand, Homespool.Model.Caller, System.Threading.CancellationToken)"/>.
    /// </returns>
    public async Task<CommandOutcome<TAnswer>?> AskAsync<TAnswer>(int printerId,
                                                                  ISendableCommand<TAnswer> commandData,
                                                                  Caller caller,
                                                                  CancellationToken cancellationToken)
    {
        CommandSendResult result = await SendAndCheckAsync(printerId, commandData, caller, cancellationToken);

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

        return new CommandOutcome<TAnswer>(result.Response!.EventType, result.Response.Reason, answer)
        {
            MachineReason = result.Response.MachineReason,
        };
    }

    /// <summary>
    /// The half the wire-typed entry points share: check the caller holds what the command requires,
    /// reach its Prusa actor, send, and turn every way the send fell short into the exception that
    /// describes it. What comes back is always a <see cref="CommandSendOutcome.Completed"/> or
    /// <see cref="CommandSendOutcome.Dispatched"/> result, which is the only distinction the
    /// callers still have to make.
    /// </summary>
    /// <remarks>
    /// <b>The downcast to <see cref="IPrinterConnectionActor"/> is the honest marker of remaining
    /// work.</b> Queries whose answers are wire payloads (<see cref="AskAsync{TAnswer}"/>) and
    /// commands constructed as Prusa types have no protocol-neutral shape yet, because no second
    /// protocol has offered one to draw it against; until then they are Prusa-only by declaration,
    /// and a link that is not the Prusa actor refuses them here rather than pretending. When a
    /// second protocol grows a comparable query, this is where the neutral shape lands.
    /// </remarks>
    private async Task<CommandSendResult> SendAndCheckAsync(int printerId,
                                                            ISendableCommand commandData,
                                                            Caller caller,
                                                            CancellationToken cancellationToken)
    {
        IPrinterLink link = await RequireLinkAsync(printerId, caller, commandData.RequiredCapability, cancellationToken);

        if (link is not IPrinterConnectionActor actor)
        {
            throw new PrinterProtocolUnsupportedException(printerId, commandData.WireName);
        }

        return Check(printerId, await actor.SendCommandAsync(commandData, cancellationToken));
    }

    /// <summary>
    /// Whether the printer's connection can carry an inline transfer, for a caller entitled to send
    /// it one - so the sender can choose between the two download commands without doing its own
    /// registry lookup around the permission check.
    /// </summary>
    /// <remarks>
    /// Same gate as a send: the caller must hold the capability the download will need, and the
    /// printer must be connected. A printer on a link that is not the Prusa actor has no notion of
    /// chunk streaming at all, and is reported as unable rather than refused - the caller's next step
    /// is a send, and that is where a foreign protocol is refused with the right exception.
    /// </remarks>
    /// <exception cref="PrinterNotConnectedException">No live connection for the printer.</exception>
    /// <exception cref="TeamAccessDeniedException">Caller lacks the capability on the printer's team.</exception>
    public async Task<bool> CanStreamChunksAsync(int printerId,
                                                 Caller caller,
                                                 Capability capability,
                                                 CancellationToken cancellationToken)
    {
        IPrinterLink link = await RequireLinkAsync(printerId, caller, capability, cancellationToken);

        return link is IPrinterConnectionActor { CanStreamChunks: true };
    }

    /// <summary>The permission check and the registry lookup every entry point shares.</summary>
    /// <remarks>
    /// <b>The capability is named by the intent or command, never by the caller.</b> A new call site
    /// therefore cannot ask for a weaker one by accident - which is the whole reason it is a property
    /// of the thing being asked for rather than an argument to this method's callers.
    /// </remarks>
    private async Task<IPrinterLink> RequireLinkAsync(int printerId,
                                                      Caller caller,
                                                      Capability capability,
                                                      CancellationToken cancellationToken)
    {
        await _access.RequireAsync(printerId, caller, capability, cancellationToken);

        if (!_registry.TryGet(printerId, out IPrinterLink? link) || link is null)
        {
            throw new PrinterNotConnectedException(printerId);
        }

        return link;
    }
}
