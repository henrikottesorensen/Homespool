using System;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect.Commands;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// A printer's connection over the pre-websocket HTTP transport, where there is no connection at
/// all: the printer POSTs telemetry on its own clock and collects a command in the response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is single-writer by construction, not by locking.</b> <see cref="SendCommandAsync"/>
/// and <see cref="TakeParkedCommand"/> are both called only from the owning
/// <see cref="PrinterConnectionActor"/>'s loop - the request thread that wants the parked command
/// posts a <see cref="TakePendingCommandMessage"/> and awaits the answer, it never reads the slot.
/// So the slot needs no synchronisation, and the class carries none, for the same reason the actor's
/// own state carries none. The one exception is <see cref="Touch"/>, which the request thread calls
/// on every POST: a monotonic timestamp with a single writer semantics that <see cref="Volatile"/>
/// covers.
/// </para>
/// <para>
/// <b><see cref="IsOpen"/> is "seen recently", and it is the printer's own cadence that sizes
/// recently.</b> Firmware on this transport polls every 4 s idle and 1 s printing (6.2.6), stretching
/// to a minute of back-off only while the server is refusing it. Three idle polls missed is a printer
/// that has gone, not one that is slow.
/// </para>
/// <para>
/// <b>Not <see cref="IChunkStreamingConnection"/>, deliberately.</b> There is no channel to stream a
/// chunk on, and firmware's own inline-request URL is an <c>assert(0)</c> in this mode. Any download
/// to a printer on this transport is a <c>START_ENCRYPTED_DOWNLOAD</c>, which it fetches itself.
/// </para>
/// </remarks>
public sealed class HttpPrinterConnection : IPrinterConnection
{
    /// <summary>
    /// How long a printer may go unheard before it counts as gone. Three of firmware's idle polls
    /// (4 s each at 6.2.6), with a little slack for a network that is slow rather than dead.
    /// </summary>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _timeProvider;
    private long _lastSeenTicks;
    private PendingCommand? _parked;

    public HttpPrinterConnection(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _lastSeenTicks = timeProvider.GetTimestamp();
    }

    /// <summary>Whether the printer has been heard from within <see cref="IdleWindow"/>.</summary>
    public bool IsOpen => _timeProvider.GetElapsedTime(Volatile.Read(ref _lastSeenTicks)) < IdleWindow;

    /// <summary>When the printer was last heard from, as a <see cref="TimeProvider"/> timestamp.</summary>
    public long LastSeen => Volatile.Read(ref _lastSeenTicks);

    /// <summary>
    /// Records that the printer just made a request. Called on every authenticated POST, before the
    /// body is even read: reaching us at all is the liveness signal.
    /// </summary>
    public void Touch()
    {
        Volatile.Write(ref _lastSeenTicks, _timeProvider.GetTimestamp());
    }

    /// <summary>
    /// Parks the command for the printer's next poll. Always <see cref="CommandHandover.Parked"/>:
    /// nothing has reached the printer yet, so the actor must not start the response clock.
    /// </summary>
    /// <remarks>
    /// The one-in-flight rule is the actor's (<c>_pending</c>), so a second park while one is waiting
    /// cannot happen from the loop that owns both. It is asserted rather than tolerated because
    /// silently overwriting a parked command would lose it with no trace - the printer would collect
    /// the second and the caller of the first would wait out the reaper.
    /// </remarks>
    public ValueTask<CommandHandover> SendCommandAsync(uint commandId, ISendableCommand command, CancellationToken cancellationToken)
    {
        if (_parked is not null)
        {
            throw new InvalidOperationException(
                $"Command {commandId} parked while {_parked.CommandId} was still waiting to be collected.");
        }

        _parked = new PendingCommand(commandId, command);

        return ValueTask.FromResult(CommandHandover.Parked);
    }

    /// <inheritdoc/>
    public PendingCommand? TakeParkedCommand()
    {
        PendingCommand? parked = _parked;

        _parked = null;

        return parked;
    }
}
