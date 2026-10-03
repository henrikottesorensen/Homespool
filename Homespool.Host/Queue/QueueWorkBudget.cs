using System;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Queue;

/// <summary>
/// How many queue passes may be doing their own work - reading and writing the database - at once.
/// A pass waiting on a printer's answer is not counted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a budget at all.</b> Passes run side by side, one per printer, so that a printer taking
/// its time to answer holds up nothing but its own queue. What passes cost together was measured
/// (2026-10-03: 24 simulated printers answering at once, 2,000 events of history each): the database
/// never refused a pass at any concurrency tried - up to 24 on a four-core VM and 16 on a Raspberry
/// Pi 3B - because a pass's cost is CPU, not lock waits. How long a pass takes follows the cores:
/// with 16 running at once its median was 27 ms on one core, 19 ms on two and 11 ms on four. A pass
/// that works while more are working than there are cores is only slower, and takes the web and the
/// printers' telemetry down with it on a small machine.
/// </para>
/// <para>
/// <b>Only the work is counted, not the pass.</b> Counting whole passes would let
/// <see cref="Environment.ProcessorCount"/> printers that have gone silent - each waiting out its
/// command's timeout - take every permit and stop every other printer's queue, which is the stall the
/// separate passes exist to prevent. A pass hands its permit back for as long as it waits on a printer
/// (<see cref="QueuePassWork.WhilePrinterAnswersAsync{T}"/>) and takes one again to go on.
/// </para>
/// <para>
/// The transfer service's own work - what a send decides and records on a printer's mailbox - is not
/// counted: it is one send, at most, for each printer a pass starts.
/// </para>
/// </remarks>
public sealed class QueueWorkBudget : IDisposable
{
    private readonly SemaphoreSlim _permits;

    /// <summary>Creates a budget of <paramref name="permits"/>.</summary>
    /// <param name="permits">How many passes may work at once; at least one.</param>
    public QueueWorkBudget(int permits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);

        Permits = permits;
        _permits = new SemaphoreSlim(permits, permits);
    }

    /// <summary>How many passes may work at once.</summary>
    public int Permits { get; }

    /// <summary>How many permits are free.</summary>
    public int Available => _permits.CurrentCount;

    /// <inheritdoc />
    public void Dispose()
    {
        _permits.Dispose();
    }

    internal Task EnterAsync(CancellationToken cancellationToken)
    {
        return _permits.WaitAsync(cancellationToken);
    }

    internal void Leave()
    {
        _permits.Release();
    }
}
