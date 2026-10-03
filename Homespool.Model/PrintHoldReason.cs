namespace Homespool.Model;

/// <summary>
/// Why a printer's queue is held behind its next file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaced a sentence, and that is the point.</b> <c>PrintFileOnPrinter.BlockedReason</c> stored
/// finished English prose, which made the column do two jobs badly: it was the only record of *what
/// happened*, and it was the text shown to whoever was reading the page. The second job meant it
/// could not be translated; the first meant the free-space check had to identify its own holds by
/// prefix-matching its own opening words, because nothing said who wrote the row.
/// </para>
/// <para>
/// <b>The prefix match was the sharper problem.</b> Translating the sentence would have left a
/// Danish string in the column and an English prefix in the comparison, so the hold would never have
/// lifted — a printer that stays stopped after somebody frees the space. A localisation change
/// causing a queue to wedge is the kind of coupling worth removing rather than working around, and
/// <c>QueueAdvancer</c>'s own comment had already asked for it: <i>"If a third writer appears, add
/// the column rather than a third prefix."</i>
/// </para>
/// <para>
/// <b>Stored as text, following <c>PrintJob.State</c>.</b> One row per file per printer, and a
/// non-null reason exists only when something is wrong — so every row of this column that is ever
/// read is read by somebody working out why a printer stopped. Integers save two bytes on rows
/// nobody reads and cost legibility on the only rows that matter. Text is also immune to the enum
/// being reordered, which earns more here than elsewhere: unlike <c>Events</c> or
/// <c>PrinterStatus</c>, this vocabulary mirrors no firmware, so nothing outside this repository
/// pins its order.
/// </para>
/// </remarks>
public enum PrintHoldReason
{
    /// <summary>Not a reason. Present so a default-valued row is not silently a real hold.</summary>
    Undefined = 0,

    /// <summary>The printer does not have room for the file.</summary>
    /// <remarks>
    /// Clears by itself: the loop re-checks, so somebody who deletes something at the panel sees the
    /// queue resume without pressing anything here.
    /// </remarks>
    InsufficientSpace = 1,

    /// <summary>
    /// The printer already holds a different file under this name, and said how big it is.
    /// </summary>
    /// <remarks>
    /// Held rather than failed, because the queue entry is still wanted - see
    /// <c>PrintFileOnPrinter</c>'s remarks on holding like a spooler.
    /// </remarks>
    FileExistsDifferentSize = 2,

    /// <summary>
    /// The printer already holds a file under this name and would not say how big it is.
    /// </summary>
    /// <remarks>
    /// A separate member rather than a null size on the one above, because the two say different
    /// things to a reader: one is demonstrably not our file, the other cannot be confirmed either
    /// way. Collapsing them would make the page claim a certainty the printer did not give.
    /// </remarks>
    FileExistsUnknownSize = 3,

    /// <summary>
    /// The print uses abrasive filament and the printer reports no hardened nozzle for it to pass
    /// through.
    /// </summary>
    /// <remarks>
    /// <b>Clears by itself, like the space hold and unlike the name ones.</b> The comparison is
    /// recomputed on every pass from what the printer last reported, so fitting a hardened nozzle
    /// and letting the printer say so lifts this with nobody pressing anything here.
    /// </remarks>
    AbrasiveFilamentNeedsHardenedNozzle = 4,

    /// <summary>
    /// The file was sliced for a printer model this one cannot print for.
    /// </summary>
    /// <remarks>
    /// Wear rather than a wasted print, which is why it holds rather than warning: a file sliced for
    /// a CoreXY carries accelerations a bed slinger should not be asked to sustain. Directional -
    /// the older machine's file on the newer one is fine and never reaches here.
    /// </remarks>
    IncompatiblePrinterModel = 5,

    /// <summary>
    /// A <c>START_PRINT</c> went unanswered, and the printer would not say afterwards whether it had
    /// taken it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one hold entered because we do not know something, rather than because we do.</b>
    /// Ordinarily an unanswered <c>START_PRINT</c> resolves in a tick or two by asking
    /// <c>SEND_JOB_INFO</c> about the job telemetry reports - see
    /// <see cref="PrintState.Unconfirmed"/>. This is what is left when that fails for
    /// <c>QueueAdvancer.StartUnresolvableAfter</c> against a printer that is connected and reporting
    /// a job it will not describe.
    /// </para>
    /// <para>
    /// <b>Holding is the only safe answer, and it is not obvious.</b> Advancing might print the file
    /// a second time, which is the defect this whole path exists for; failing the entry would throw
    /// away a print that may never have run. So the queue stops with a sentence, and a person -
    /// who can walk to the machine, which is more than the loop can do - decides.
    /// </para>
    /// <para>
    /// <b>Its exit is a person, like the name collisions and unlike the space hold.</b> Cancelling
    /// the entry moves the queue past it; queueing the file again clears it, because asking for it a
    /// second time is somebody saying they have looked.
    /// </para>
    /// </remarks>
    PrintStartUnresolved = 6,

    /// <summary>
    /// The printer refused the transfer the same way, several times running, with nothing between
    /// the attempts changing its answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A bound on retrying, not a classification of the reason.</b> The transfer path retries any
    /// refusal it does not recognise, because treating an unread string as terminal would throw away a
    /// print that a moment's wait would have sent. That default has no end on its own: a refusal that
    /// never changes - <c>STORAGE_FAILURE</c> for a filename firmware cannot create is the one seen -
    /// was retried every few seconds for as long as anybody left it. This is where it stops, and it
    /// covers codes nobody has seen yet, which a list of terminal reasons could not.
    /// </para>
    /// <para>
    /// <b>The printer's own words go with it</b>, in
    /// <c>PrintFileOnPrinter.TransferRefusalReason</c>, because they are the useful part: a person can
    /// read <i>"Failed to create directory"</i> once and act on it.
    /// </para>
    /// <para>
    /// <b>Its exit is a person, like <see cref="PrintStartUnresolved"/>.</b> Waiting has already been
    /// tried, so nothing here re-attempts the transfer. Cancelling the entry moves the queue past it;
    /// queueing the file again clears it and starts a fresh count, because asking a second time is
    /// somebody saying they have looked.
    /// </para>
    /// </remarks>
    TransferRefused = 7,

    /// <summary>
    /// The file is still in this server's storage, but could not be opened to send it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fault on this side, not the printer's</b> - file permissions, or a failing disk. A file
    /// that could not be opened because it had just been deleted never reaches here: the queue entry is
    /// dropped instead, as it is for a file found missing before the send.
    /// </para>
    /// <para>
    /// <b>Storage that is not there at all comes here too.</b> When the owner's whole directory is
    /// missing - an unmounted volume, an empty mount point - the file is not taken to be deleted,
    /// since deleting a file never removes its directory, and the entry waits for the storage instead
    /// of being dropped.
    /// </para>
    /// <para>
    /// <b>Clears by itself, like the space hold.</b> Whatever fixes it happens outside Homespool, so
    /// the loop tries the file again every <c>QueueAdvancer.BlockRecheckAfter</c>, and the first send
    /// that can open it lifts the hold. Waiting for a person to re-queue as well would make fixing the
    /// storage only half the remedy.
    /// </para>
    /// </remarks>
    FileUnreadable = 8,

    /// <summary>
    /// Somebody stopped the file's transfer at the printer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A person's decision, so the queue does not argue with it.</b> Firmware reports
    /// <c>TRANSFER_STOPPED</c> when a transfer is stopped deliberately - from the printer's own
    /// screen - and removes the partial. Sending the file again would undo what somebody standing at
    /// the machine just did, and would go on undoing it for as long as they kept pressing stop.
    /// </para>
    /// <para>
    /// <b>Its exit is a person, like <see cref="TransferRefused"/>.</b> Cancelling the entry moves the
    /// queue past it; queueing the file again clears it, because asking for it a second time is
    /// somebody saying they want it after all.
    /// </para>
    /// </remarks>
    TransferStopped = 9,

    /// <summary>
    /// The printer took the file's transfer and gave it up, several times running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="TransferRefused"/>'s bound, for the other way a transfer fails.</b> A refusal is
    /// the printer saying no to the command; an abort is it saying yes and then abandoning the
    /// download - <c>TRANSFER_ABORTED</c>, the partial removed. An abort is offered again after the
    /// same waits, and the same number of them in a row holds here. Without that a file that aborts
    /// every time would be sent for ever, each attempt a transfer's worth of traffic.
    /// </para>
    /// <para>
    /// <b>Its exit is a person</b>, as for the refusals: cancelling moves past it, queueing again
    /// clears it and starts a fresh count.
    /// </para>
    /// </remarks>
    TransferAborted = 10,

    /// <summary>
    /// The file is too large for a printer to be sent: 4 GiB or more, which firmware cannot be told
    /// the size of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fact about the file, so waiting cannot change it.</b> Without this hold the send went out
    /// anyway with a size firmware cannot read, the printer refused it, and the refusals were counted
    /// and held as <see cref="TransferRefused"/> - a hold that blamed the printer for a file it was
    /// never going to take. Nothing is offered, and no older copy on the drive is deleted for it.
    /// </para>
    /// <para>
    /// <b>Its exit is a person, like <see cref="TransferStopped"/>.</b> Cancelling the entry moves the
    /// queue past it; queueing the file again - after replacing it with a smaller one - clears it.
    /// </para>
    /// </remarks>
    FileTooLarge = 11,
}
