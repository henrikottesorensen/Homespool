using Homespool.Model;

namespace Homespool.Host.Queue;

/// <summary>Why the loop is holding, so a log line or a UI can say something true.</summary>
/// <remarks>
/// <b>Numbered explicitly, though nothing stores one.</b> The values have already moved once without
/// anybody deciding to move them - <see cref="IncompatibleWithPrinter"/> was added in the middle,
/// which renumbered the two below it. Nothing broke, because this reason only ever becomes a
/// localised sentence or a log line; but "nothing broke" was luck rather than design, and it is the
/// kind of luck that runs out the first time a value is written down somewhere. Pinning costs a
/// line and removes the question.
/// </remarks>
public enum QueueWaitReason
{
    Undefined = 0,

    /// <summary>A file is being pulled from us; firmware allows only one transfer at a time.</summary>
    Transferring = 1,

    /// <summary>
    /// The head will not fit on the printer's drive, so nothing can be sent until somebody frees
    /// space. The queue holds behind it rather than skipping past - spooler behaviour.
    /// </summary>
    InsufficientSpace = 2,

    /// <summary>The bytes arrived but no <c>FILE_INFO</c> has named the path to print.</summary>
    AwaitingPrinterPath = 3,

    /// <summary>
    /// The file and the printer's own report of its hardware disagree in a way that costs more than
    /// a bad print - abrasive filament with no hardened nozzle, or a file sliced for a machine this
    /// one is not.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not routed back into the transfer path</b>, unlike
    /// <see cref="InsufficientSpace"/>. That one has to be, because only the transfer path re-asks
    /// the drive how much room there is; this one is recomputed from rows already in hand on every
    /// pass, so it clears itself without anything being attempted.
    /// </remarks>
    IncompatibleWithPrinter = 4,

    /// <summary>
    /// The printer is idle, or holding a print nobody has cleared, and nobody has offered it up for
    /// work. Includes a finished print still on the bed, which is the case with no backstop under it.
    /// </summary>
    /// <remarks>
    /// <b>Only the states firmware would accept the flag from</b> - a machine that is busy is
    /// <see cref="PrinterBusy"/>, not this. The distinction is the whole reason this value is loud:
    /// it is the one wait a person clears by saying the sheet is empty, and
    /// <c>QueueWaitDescription.NeedsAPerson</c> puts a button beside it on that basis. Offering that
    /// button where the printer would refuse the press names a remedy that cannot work.
    /// </remarks>
    PrinterNotAvailable = 5,

    /// <summary>
    /// A print of ours is open on this printer - commanded and not yet ended.
    /// </summary>
    /// <remarks>
    /// <b>Usually the few seconds in which a printer that has accepted a print still says
    /// <c>READY</c></b>, and occasionally longer: a <c>START_PRINT</c> that went unanswered holds
    /// the same slot while the loop asks the printer what it is doing
    /// (<c>PrintState.Unconfirmed</c>). The queue's answer is the same either way - a print is in
    /// hand, so do not start another - which is why one reason covers both.
    /// </remarks>
    PrintStarting = 6,

    /// <summary>
    /// A <c>START_PRINT</c> went unanswered, and the printer would not say afterwards whether it had
    /// taken it.
    /// </summary>
    /// <remarks>
    /// The queue stops rather than risk printing the file twice, and only a person can move it -
    /// see <see cref="PrintHoldReason.PrintStartUnresolved"/>. Like the other holds it has no
    /// sentence of its own here, because the hold banner carries one; giving it both would put two
    /// voices on the page saying the same thing.
    /// </remarks>
    PrintStartUnresolved = 7,

    /// <summary>
    /// The printer is occupied by something that is not a print of ours - printing, paused, in
    /// attention, or in error. The queue waits, and there is nothing for anybody to do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Split off <see cref="PrinterNotAvailable"/> because the two want opposite treatment.</b>
    /// Both are the printer being unavailable and both are correct waits; only one of them is
    /// cleared by a person saying the sheet is empty. This one is cleared by waiting, and firmware
    /// refuses <c>SET_PRINTER_READY</c> from every state it covers
    /// (<c>remote_print_ready</c>, printer_state.cpp:561-577), so a button offering it would name a
    /// remedy the machine declines.
    /// </para>
    /// <para>
    /// <b>Most often a print nobody told us about</b>: a panel start, or one begun from a file this
    /// loop staged for an earlier job, neither of which leaves a <c>PrintJob</c> row to make
    /// <see cref="PrintStarting"/> true. Seen on hardware with a queued file sitting behind a print
    /// at 82%, under a banner asking for the printer to be made ready.
    /// </para>
    /// <para>
    /// <b>No sentence of its own</b>, like the two holds above it: the status card carries the
    /// progress bar, the attention reason or the error already, and a queue repeating that would be
    /// a second voice. See <c>QueueWaitDescription</c>.
    /// </para>
    /// </remarks>
    PrinterBusy = 8,
}
