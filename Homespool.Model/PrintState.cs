namespace Homespool.Model;

/// <summary>
/// Where a print has got to, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// <b>Was <c>PrintOutcome</c> until 2026-08-04, and the rename is the whole of the fix.</b> A row
/// exists while the print runs (<see cref="Entities.PrintJob"/> is opened at <c>START_PRINT</c>), so
/// the enum describes the middle as well as the end - which the old name denied, forcing a paragraph
/// here conceding it was "a state machine rather than only an outcome, despite the name".
/// </para>
/// <para>
/// <b>The shape was never the problem.</b> Every spooler in
/// the lineage puts active states in the job's own state enum: PrusaLink's vendored <c>Job</c>
/// schema is <c>PRINTING, PAUSED, FINISHED, STOPPED, ERROR</c>, and IPP/CUPS spans <c>pending</c>
/// through <c>completed</c> in one attribute. Only the name here promised an ending it did not
/// deliver.
/// </para>
/// <para>
/// In this project's root beside <see cref="PrinterStatus"/> and <see cref="PrinterType"/> rather than
/// under <c>Entities</c>, because it is a persisted column type rather than a wire vocabulary.
/// Unlike <see cref="PrinterStatus"/> it is
/// <b>ours</b>, not Prusa's vocabulary, which is the company <see cref="PrinterType"/> keeps.
/// </para>
/// </remarks>
public enum PrintState
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>
    /// Commanded, and nobody has said whether the printer took it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A timeout is not a negative answer, and for <c>START_PRINT</c> the timeout is <i>caused
    /// by</i> success</b> (hardware, 2026-08-21): the printer is slow to acknowledge precisely
    /// because it accepted the command and went off to home and heat. A loop recording that as "the
    /// print did not happen" leaves the queue entry in place, and the same file prints a second time
    /// the moment somebody makes the printer ready.
    /// </para>
    /// <para>
    /// <b>So the row is opened before the command is sent, and this is what it says until the
    /// printer settles it.</b> Distinct from <see cref="Starting"/>, which means <i>accepted</i> -
    /// conflating the two would trade one wrong assertion for its mirror image, dropping a queue
    /// entry for a print that never began. It is resolved by asking <c>SEND_JOB_INFO</c> about the
    /// job telemetry reports, never by assuming: a timeout is not a negative answer.
    /// </para>
    /// <para>
    /// <b>A row in this state is not history yet.</b> It is the loop's record of an outstanding
    /// question, and it is <i>removed</i> if the answer turns out to be "no print" - the one state
    /// here whose rows do not always end up closed. Nothing outside the loop writes it.
    /// </para>
    /// <para>
    /// It sits here because this is where it belongs in a print's life, and carries 7 because that
    /// is what was free. The column stores names, so the number is not what a row says.
    /// </para>
    /// </remarks>
    Unconfirmed = 7,

    /// <summary>
    /// Commanded and accepted, but the printer has not reported itself printing yet.
    /// </summary>
    /// <remarks>
    /// <b>Not a formality - a print really does take seconds to begin.</b> The Core One capture has
    /// <c>START_PRINT</c> at +83.7 s and <c>READY -> PRINTING</c> at +86.8 s, because firmware passes
    /// through preview-init and heating first; only the FakePrinter transitions instantly. Without
    /// this value, "has not started printing" and "has stopped printing" are the same observation, and
    /// a loop closing rows on "no longer printing" would close every print moments after starting it.
    /// <para>
    /// <b>Those three seconds are one machine's number, not a guarantee</b> (hardware, 2026-08-04):
    /// an MK3.5 at <c>6.5.7+12836</c> reports <c>PRINTING</c> in the <i>first</i> sample after
    /// <c>START_PRINT</c>, nozzle still cold, so this phase can be zero samples wide. What earns the
    /// two phases is the guard - a row must never close before it was seen printing - and not the
    /// width of the gap.
    /// </para>
    /// <para>
    /// <b>And <c>Printing</c> here does not mean extruding.</b> On that same run, plastic began
    /// 168 s after <c>START_PRINT</c> - homing, mesh probing and heating first - which is why
    /// <c>FilamentUsed</c> increasing, never a state change, is the signal for when a print actually
    /// begins.
    /// </para>
    /// </remarks>
    Starting = 1,

    /// <summary>
    /// The printer has reported itself printing. Paired with a null <c>EndedAt</c>.
    /// </summary>
    /// <remarks>
    /// <b>Reached by observation, not by assumption</b> - a row only leaves <see cref="Starting"/>
    /// once telemetry actually says <c>PRINTING</c>. That is what makes "no longer printing" mean
    /// something: a row that never got here was never printing, so it cannot have finished.
    /// </remarks>
    Printing = 2,

    /// <summary>The printer reached its finished screen.</summary>
    Finished = 3,

    /// <summary>
    /// Cancelled - here or at the panel, which <c>PrintJob.StoppedByUserId</c> tells apart, naming
    /// the person when it was here.
    /// </summary>
    Stopped = 4,

    /// <summary>Refused, or ended somewhere nothing else explains. <c>PrintJob.Reason</c> says what.</summary>
    Failed = 5,

    /// <summary>
    /// It stopped being observable without saying how - most plausibly a restart across which the
    /// firmware job id mapping was lost. Recorded as its own outcome rather than guessed at, and
    /// distinct from <see cref="Undefined"/>, which means nobody looked.
    /// </summary>
    Unknown = 6,
}
