namespace Homespool.Host.Queue;

/// <summary>What to do about a print that was commanded and never acknowledged.</summary>
public enum PrintStartVerdict
{
    /// <summary>Never set.</summary>
    Undefined = 0,

    /// <summary>Still unknown. Leave everything as it is and ask again next pass.</summary>
    /// <remarks>
    /// The default answer, and the one every gap in the evidence falls into. Waiting costs a tick;
    /// the other three verdicts each throw something away.
    /// </remarks>
    KeepWaiting = 1,

    /// <summary>
    /// The printer is printing our file. Adopt it: the row becomes a real print and the queue entry
    /// has done its job.
    /// </summary>
    Started = 2,

    /// <summary>
    /// The command did not take. Drop the row we opened; the entry is still queued and the next pass
    /// sends it again.
    /// </summary>
    NeverStarted = 3,

    /// <summary>
    /// The printer will not say, and has not for long enough that it is not going to.
    /// </summary>
    /// <remarks>
    /// <b>Reached only by a connected printer that reports a job and refuses to describe it</b>, for
    /// <c>QueueAdvancer.StartUnresolvableAfter</c>. Neither advancing nor failing is safe, so the
    /// queue holds and a person decides - see <see cref="Homespool.Model.PrintHoldReason.PrintStartUnresolved"/>.
    /// </remarks>
    Unresolvable = 4,
}
