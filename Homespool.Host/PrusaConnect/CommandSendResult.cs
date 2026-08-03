using System.Text.Json;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// What became of one <c>SendCommandAsync</c> call: how far it got, and the printer's answer if it
/// got all the way.
/// </summary>
/// <param name="Outcome">How far the command got. See <see cref="CommandSendOutcome"/>.</param>
/// <param name="Response">
/// The printer's answer, present only for <see cref="CommandSendOutcome.Completed"/> - the other
/// outcomes are precisely the cases where no answer arrived.
/// </param>
/// <param name="Data">
/// The answering event's <c>data</c> object, verbatim and unparsed, or null when it carried none -
/// which is all but the commands that asked a question.
/// <para>
/// <b>This is as far as an untyped payload travels, and that is the point.</b> The transport cannot
/// type it - correlation is on <c>command_id</c> alone and nothing here knows which command asked
/// what - but a <c>JsonElement</c> handed to callers is a schema that ends up living in call sites.
/// So it stops one layer up: <see cref="PrinterCommandService.AskAsync"/> deserialises it into the
/// shape the command declared through <see cref="Commands.ISendableCommand{TAnswer}"/>, and nothing
/// above that ever sees this property. <c>CommandSendResult</c> reaches exactly one production
/// class, which is what makes that enforceable rather than merely encouraged.
/// </para>
/// <para>
/// <b>Safe to hold past the read loop.</b> This is <c>EventDTO.Data</c>, which deserialisation backs
/// with a document of its own rather than a buffer the loop goes on to reuse. The sink already
/// depends on that: <c>TelemetryWriter</c> formats the same element off its channel, on another
/// thread, well after the connection has moved on.
/// </para>
/// <para>
/// Defaulted, unlike <see cref="Response"/>, because seven of the eight places this is constructed
/// describe an answer that never arrived - no socket, no free slot, nothing written, nothing back -
/// and there is no payload for them to forget. The one site that has data is the one that must pass
/// it.
/// </para>
/// </param>
public sealed record CommandSendResult(CommandSendOutcome Outcome, CommandOutcome? Response, JsonElement? Data = null);
