using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Write-only view of a printer's live connection. Command-sending code has no business touching
/// the receive side, <c>State</c> transitions, or the close handshake - only handing over a command.
/// Commands come from the printer's <see cref="PrinterConnectionActor"/> loop, and only from there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Takes the command, not its bytes.</b> Encoding is the connection's business, because the two
/// transports frame the same body differently: the WebSocket wraps it in a 9-byte header and writes
/// it now; the pre-websocket HTTP transport holds it until the printer's next telemetry POST and
/// returns it in that response, typed by <c>Content-Type</c>. A caller that had encoded a frame
/// would have chosen a transport it is not supposed to know about.
/// </para>
/// <para>
/// <b>Transfer chunks are deliberately not here.</b> They exist on <see cref="IChunkStreamingConnection"/>,
/// which only a socket implements: the inline transfer engine is a WebSocket mechanism with no HTTP
/// counterpart, and firmware itself asserts on it in non-websocket mode. Keeping it off this
/// interface makes "unreachable over HTTP" a property of the type rather than a runtime refusal.
/// </para>
/// </remarks>
public interface IPrinterConnection
{
    bool IsOpen { get; }

    /// <summary>
    /// Hands one command to the printer. On a socket, that is a write; on the HTTP transport, it is
    /// parking the command for the printer's next poll.
    /// </summary>
    /// <param name="commandId">The id the printer will echo in its answering event.</param>
    /// <param name="command">The command; encoded by the implementation.</param>
    /// <param name="cancellationToken">Cancels a wait for the write lock. Never a partial write.</param>
    /// <returns>
    /// Whether the printer has the command now, or will collect it. The actor starts a command's
    /// response deadline only once it has actually been delivered, so a connection that parks must
    /// say so - a parked command timing out against the moment it was parked would report a healthy
    /// printer as unresponsive for the length of its own poll interval.
    /// </returns>
    ValueTask<CommandHandover> SendCommandAsync(uint commandId, ISendableCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// The command parked on this connection, if any, removed from it - the printer is collecting.
    /// A socket never has one.
    /// </summary>
    /// <remarks>
    /// Called only from the actor loop, in answer to a <see cref="TakePendingCommandMessage"/>. That
    /// is what keeps the slot loop-owned despite living on the connection: it is written by
    /// <see cref="SendCommandAsync"/> from the loop and emptied here from the loop, and the request
    /// thread that wants the command only ever posts a message and awaits the answer.
    /// </remarks>
    PendingCommand? TakeParkedCommand();
}

/// <summary>What became of a command handed to a connection.</summary>
public enum CommandHandover
{
    /// <summary>Written to the printer; the response clock may start.</summary>
    Written,

    /// <summary>Held for the printer's next poll; the response clock waits for the hand-over.</summary>
    Parked,
}

/// <summary>
/// A command a printer is collecting over the HTTP transport: what it asked to be sent, and the id
/// it will echo when it answers.
/// </summary>
public sealed record PendingCommand(uint CommandId, ISendableCommand Command);
