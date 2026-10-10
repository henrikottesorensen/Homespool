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
    /// Takes the command parked for this printer, or null for nothing to send - nothing pending, or a
    /// connection being torn down. Throws <see cref="System.OperationCanceledException"/> if the
    /// request ended, after giving back whatever it took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The take is awaited without the request's token, deliberately.</b> Once the loop has the
    /// message it empties the slot and stamps the response clock whether or not anyone is still
    /// listening, so abandoning the wait would lose the command. The wait is short and always ends:
    /// the loop drains every queued message, teardown included, and does no I/O on this transport.
    /// The delivery's answer is awaited the same way, for the same reason.
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
    /// <para>
    /// <b>A command goes out only once the loop has agreed.</b> A teardown reports whatever is in
    /// flight as never having left, so a command taken just before one began - its delivery refused
    /// by the draining loop, or posted to a mailbox already closed - stays out of the response, and
    /// the printer is not handed something its caller has been told it never got.
    /// </para>
    /// </remarks>
    public static async Task<PendingCommand?> CollectAsync(IPrinterConnectionActor actor, CancellationToken requestAborted)
    {
        TaskCompletionSource<PendingCommand?> take = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await actor.PostAsync(new TakePendingCommandMessage(take), requestAborted);
        }
        catch (ChannelClosedException)
        {
            // Torn down before the poll reached the loop: nothing was taken, and the teardown
            // settles whatever is parked.
            return null;
        }

        PendingCommand? pending = await take.Task;
        bool aborted = requestAborted.IsCancellationRequested;

        if (aborted)
        {
            if (pending is not null)
            {
                await TellAsync(actor, new ReturnCollectedCommandMessage(pending));
            }

            throw new OperationCanceledException(requestAborted);
        }

        if (pending is null)
        {
            return null;
        }

        TaskCompletionSource<bool> delivery = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!await TellAsync(actor, new CommandDeliveredMessage(pending, delivery)))
        {
            return null;
        }

        return await delivery.Task ? pending : null;
    }

    /// <summary>
    /// Posts how a taken command was settled. False if the connection is being torn down, which
    /// fails the command as <see cref="Printing.CommandSendOutcome.NotConnected"/> - what giving it
    /// back would have led to anyway, and what keeping it out of the response makes true.
    /// </summary>
    private static async Task<bool> TellAsync(IPrinterConnectionActor actor, ConnectionMessage settled)
    {
        try
        {
            await actor.PostAsync(settled, CancellationToken.None);

            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }
}
