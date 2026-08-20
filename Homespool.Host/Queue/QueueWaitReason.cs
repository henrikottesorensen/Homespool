namespace Homespool.Host.Queue;

/// <summary>Why the loop is holding, so a log line or a UI can say something true.</summary>
public enum QueueWaitReason
{
    Undefined = 0,

    /// <summary>A file is being pulled from us; firmware allows only one transfer at a time.</summary>
    Transferring,

    /// <summary>
    /// The head will not fit on the printer's drive, so nothing can be sent until somebody frees
    /// space. The queue holds behind it rather than skipping past - spooler behaviour.
    /// </summary>
    InsufficientSpace,

    /// <summary>The bytes arrived but no <c>FILE_INFO</c> has named the path to print.</summary>
    AwaitingPrinterPath,

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
    IncompatibleWithPrinter,

    /// <summary>
    /// The printer is not <c>Ready</c>. Includes a finished print nobody has cleared, which is the
    /// case with no backstop under it.
    /// </summary>
    PrinterNotAvailable,

    /// <summary>
    /// A print has been commanded and the printer has not reported itself printing yet - the few
    /// seconds in which it still says <c>READY</c>.
    /// </summary>
    PrintStarting,
}
