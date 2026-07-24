using System.Collections.Concurrent;
using System.Threading.Tasks;

using PrinterService.Model;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// The printer's answer to a sent command, e.g. <c>(Finished, null)</c> or
/// <c>(Rejected, "No print to pause")</c> - confirmed against firmware source
/// (Prusa-Firmware-Buddy planner.cpp:667-790 at the pinned ref): a command's outcome always arrives
/// as an ordinary event carrying the same <c>command_id</c>, not a distinct reply channel.
/// </summary>
public sealed record CommandOutcome(Events EventType, string? Reason);

/// <summary>
/// Correlates a sent command with the event that answers it, one pending command per printer -
/// matching the firmware's own single-in-flight-command limit (connect.cpp:469-476).
/// </summary>
public interface IPrinterCommandCorrelator
{
    /// <summary>Registers <paramref name="commandId"/> as in-flight for <paramref name="printerId"/>.
    /// False, with no task produced, if one is already pending.</summary>
    bool TryBeginCommand(int printerId, uint commandId, out Task<CommandOutcome> outcome);

    /// <summary>Called from <see cref="MessageDispatcher"/> for every event. Completes the pending
    /// task if <paramref name="commandId"/> matches what's tracked for <paramref name="printerId"/>.</summary>
    void ObserveEvent(int printerId, uint? commandId, Events eventType, string? reason);

    void Cancel(int printerId);
}

public sealed class PrinterCommandCorrelator : IPrinterCommandCorrelator
{
    private sealed record Pending(uint CommandId, TaskCompletionSource<CommandOutcome> Tcs);

    private readonly ConcurrentDictionary<int, Pending> _pending = new();

    public bool TryBeginCommand(int printerId, uint commandId, out Task<CommandOutcome> outcome)
    {
        // RunContinuationsAsynchronously: TrySetResult below runs on the WebSocket receive loop
        // (MessageDispatcher.Dispatch, via ObserveEvent). Without this flag, whatever is awaiting
        // outcome - PrinterCommandTransport.SendAsync, and its own callers - would resume
        // synchronously on that same thread, delaying the next inbound message until it's done.
        TaskCompletionSource<CommandOutcome> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool began = _pending.TryAdd(printerId, new Pending(commandId, tcs));

        outcome = tcs.Task;

        return began;
    }

    public void ObserveEvent(int printerId, uint? commandId, Events eventType, string? reason)
    {
        if (_pending.TryGetValue(printerId, out Pending? pending) && commandId == pending.CommandId)
        {
            _pending.TryRemove(printerId, out _);
            pending.Tcs.TrySetResult(new CommandOutcome(eventType, reason));
        }
    }

    public void Cancel(int printerId)
    {
        if (_pending.TryRemove(printerId, out Pending? pending))
        {
            pending.Tcs.TrySetCanceled();
        }
    }
}
