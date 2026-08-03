using System;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Queue;

/// <summary>
/// A poke telling the queue advancer that something changed and it need not wait for its timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The timer is the mechanism and this is the optimisation</b>, not the other way round. Everything
/// the loop reacts to is persisted - the queue, the printer's live state, the events - so a missed
/// poke costs a tick's delay and nothing else. That is what lets the signal be lossy, in-memory and
/// free of any ordering guarantee: it never carries information, only urgency.
/// </para>
/// <para>
/// Two things poke it: enqueueing (somebody pressed Queue and expects it to happen now, not in five
/// seconds) and a printer connecting (its queue may have been waiting since before the last restart).
/// </para>
/// <para>
/// Coalescing rather than counting - many pokes between ticks wake the loop once, because the loop
/// re-reads everything anyway and there is no per-poke work to do.
/// </para>
/// </remarks>
public sealed class QueueSignal : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Wakes the advancer, if it is waiting. Never blocks and never throws.</summary>
    public void Poke()
    {
        // Capacity 1: a second poke before the loop wakes finds the semaphore full and is dropped,
        // which is the coalescing. Release would throw on overflow, hence the guard rather than a
        // try/catch around the common path.
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Two pokes raced the check. One wake-up is what both wanted.
            }
        }
    }

    /// <summary>
    /// Waits for a poke or for <paramref name="interval"/> to pass, whichever comes first.
    /// </summary>
    /// <returns>Nothing meaningful - the caller re-reads state regardless of why it woke.</returns>
    public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        await _wake.WaitAsync(interval, cancellationToken);
    }

    public void Dispose()
    {
        _wake.Dispose();
    }
}
