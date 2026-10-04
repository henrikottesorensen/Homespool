namespace Homespool.Host.Queue;

/// <summary>What came of withdrawing an entry from a printer's queue.</summary>
public enum QueueCancellation
{
    /// <summary>Never set.</summary>
    Undefined = 0,

    /// <summary>
    /// There was no such entry on that printer - never queued, already withdrawn, or already started.
    /// </summary>
    NotFound = 1,

    /// <summary>The entry is gone, and nothing had been started from it.</summary>
    Removed = 2,

    /// <summary>
    /// The entry is gone, but its print was already being started, so it is stopped if it turns out
    /// to have started.
    /// </summary>
    /// <remarks>
    /// <b>Not stopped yet, and not certain to need stopping.</b> The printer had been told to start it
    /// and had not answered, so nothing can be stopped this instant; the queue's loop sends the stop
    /// once the printer says the print is running, or drops the request if it never began.
    /// </remarks>
    StopRequested = 3,
}
