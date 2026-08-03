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
