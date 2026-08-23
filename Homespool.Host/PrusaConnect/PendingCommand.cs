using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// A command a printer is collecting over the HTTP transport: what it asked to be sent, and the id
/// it will echo when it answers.
/// </summary>
/// <remarks>
/// <b>Only the HTTP transport ever has one.</b> A socket writes the frame and is done, so its
/// <see cref="IPrinterConnection.TakeParkedCommand"/> always answers null; this exists because a
/// printer that polls has to be handed the command in the response to its own next telemetry POST.
/// The id travels with it rather than being re-derived, since it is what correlates the answering
/// event and was allocated when the command was accepted, not when it is collected.
/// </remarks>
public sealed record PendingCommand(uint CommandId, ISendableCommand Command);
