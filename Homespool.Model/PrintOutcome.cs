namespace Homespool.Model;

/// <summary>
/// Where a print has got to, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>A state machine rather than only an outcome</b>, despite the name: a row exists while the print
/// runs (<see cref="Entities.PrintJob"/> is opened at <c>START_PRINT</c>), so the enum has to describe
/// the middle as well as the end.
/// </para>
/// <para>
/// In this project's root beside <see cref="PrinterStatus"/> and <see cref="PrinterType"/> rather than
/// under <c>Entities</c>, because it is a persisted column type - the arrangement
/// <c>notes/protocol-vocabulary-boundary.md</c> describes. Unlike <see cref="PrinterStatus"/> it is
/// <b>ours</b>, not Prusa's vocabulary, which is the company <see cref="PrinterType"/> keeps.
/// </para>
/// </remarks>
public enum PrintOutcome
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>
    /// Commanded and accepted, but the printer has not reported itself printing yet.
    /// </summary>
    /// <remarks>
    /// <b>Not a formality - a print really does take seconds to begin.</b> The Core One capture has
    /// <c>START_PRINT</c> at +83.7 s and <c>READY -> PRINTING</c> at +86.8 s, because firmware passes
    /// through preview-init and heating first; only the FakePrinter transitions instantly. Without
    /// this value, "has not started printing" and "has stopped printing" are the same observation, and
    /// a loop closing rows on "no longer printing" would close every print moments after starting it.
    /// </remarks>
    Starting,

    /// <summary>
    /// The printer has reported itself printing. Paired with a null <c>EndedAt</c>.
    /// </summary>
    /// <remarks>
    /// <b>Reached by observation, not by assumption</b> - a row only leaves <see cref="Starting"/>
    /// once telemetry actually says <c>PRINTING</c>. That is what makes "no longer printing" mean
    /// something: a row that never got here was never printing, so it cannot have finished.
    /// </remarks>
    Printing,

    /// <summary>The printer reached its finished screen.</summary>
    Finished,

    /// <summary>Cancelled - by us or at the panel, which <c>PrintJob.StoppedByUs</c> tells apart.</summary>
    Stopped,

    /// <summary>Refused, or ended somewhere nothing else explains. <c>PrintJob.Reason</c> says what.</summary>
    Failed,

    /// <summary>
    /// It stopped being observable without saying how - most plausibly a restart across which the
    /// firmware job id mapping was lost. Recorded as its own outcome rather than guessed at, and
    /// distinct from <see cref="Undefined"/>, which means nobody looked.
    /// </summary>
    Unknown,
}
