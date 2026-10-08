using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The HTTP transport's half of collecting a parked command: asks the actor for it on behalf of a
/// telemetry POST, then gives it back if that POST ended before its response could carry it, or
/// confirms that the response will.
/// </summary>
public static class HttpCommandCollection
{
    /// <summary>
    /// Takes the command parked for this printer, or null for nothing pending. Throws
    /// <see cref="System.OperationCanceledException"/> if the request ended, after giving back
    /// whatever it took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The take is awaited without the request's token, deliberately.</b> Once the loop has the
    /// message it empties the slot and stamps the response clock whether or not anyone is still
    /// listening, so abandoning the wait would lose the command. The wait is short and always ends:
    /// the loop drains every queued message, teardown included, and does no I/O on this transport.
    /// </para>
    /// <para>
    /// <b>Only an end before the response is caught.</b> The check runs before anything is written.
    /// A connection lost during the write cannot be told apart from a delivered command, since the
    /// write completing proves only that the proxy in front of us has the bytes, so that command
    /// stays delivered and the printer's answer, or its absence, decides.
    /// </para>
    /// <para>
    /// <b>The request is read once, and the actor told the result either way</b> - given back, or
    /// confirmed as going out. Read twice, a request ending between the reads would be confirmed and
    /// then thrown away, and the actor would count as delivered a command no response carried.
    /// </para>
    /// </remarks>
    public static async Task<PendingCommand?> CollectAsync(IPrinterConnectionActor actor, CancellationToken requestAborted)
    {
        TaskCompletionSource<PendingCommand?> take = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await actor.PostAsync(new TakePendingCommandMessage(take), requestAborted);

        PendingCommand? pending = await take.Task;
        bool aborted = requestAborted.IsCancellationRequested;

        if (pending is not null)
        {
            ConnectionMessage settled = aborted ?
                new ReturnCollectedCommandMessage(pending) :
                new CommandDeliveredMessage(pending);

            try
            {
                await actor.PostAsync(settled, CancellationToken.None);
            }
            catch (ChannelClosedException)
            {
                // The connection is being torn down, and the teardown fails the command as
                // NotConnected - which is what giving it back would have led to anyway.
            }
        }

        if (aborted)
        {
            throw new OperationCanceledException(requestAborted);
        }

        return pending;
    }
}
