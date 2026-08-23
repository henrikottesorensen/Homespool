namespace Homespool.Host.Queue;

/// <summary>
/// What asking the printer about its current job established about a print we commanded and never
/// heard back about.
/// </summary>
/// <remarks>
/// <b>Four answers, not two, and the fourth is the one that keeps this honest.</b> A question can
/// come back unanswered, or answered about a job the printer only remembers - neither of which is
/// evidence either way. Collapsing <see cref="Inconclusive"/> into <see cref="NoJob"/> would be the
/// original defect wearing a new hat: treating "nobody said" as "it did not happen".
/// </remarks>
public enum JobAnswer
{
    /// <summary>Nobody looked.</summary>
    Undefined = 0,

    /// <summary>
    /// Nothing to ask with - telemetry reports no job id, so there is no <c>SEND_JOB_INFO</c> to
    /// send.
    /// </summary>
    /// <remarks>
    /// Firmware answers only about a job named by id, so a printer that reports none cannot be
    /// questioned at all. That is not silence: combined with a fresh telemetry sample and a
    /// not-printing status, it is what the elapsed-time rule reads as "the command did not take".
    /// </remarks>
    NotAsked = 1,

    /// <summary>The printer named the file we sent it. The print is ours.</summary>
    Ours = 2,

    /// <summary>
    /// The printer named a different file, so whatever is running was not started by our command.
    /// </summary>
    /// <remarks>
    /// A definite negative rather than a puzzle: somebody printed from the panel, or from
    /// PrusaLink. Our entry stays queued and waits for the printer like any other.
    /// </remarks>
    SomebodyElses = 3,

    /// <summary>
    /// The printer says it has no job at all - firmware's <c>"No job in progress"</c>.
    /// </summary>
    /// <remarks>
    /// <b>The only definite negative on the wire.</b> A status can be stale by a telemetry interval
    /// and a timeout says nothing; this is the machine stating in an answer of its own that there is
    /// nothing running.
    /// </remarks>
    NoJob = 4,

    /// <summary>
    /// Asked, and no wiser: the printer would not answer, or described a job it merely remembers -
    /// a <c>FIN_OK</c> carrying no file name to compare.
    /// </summary>
    /// <remarks>
    /// Also where an unrecognised refusal lands. Firmware's wording is free to change between
    /// releases, so a reason nobody has read yet must mean "ask again", never a verdict.
    /// </remarks>
    Inconclusive = 5,
}
