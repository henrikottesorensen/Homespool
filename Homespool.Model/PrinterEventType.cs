namespace Homespool.Model;

/// <summary>
/// What a <see cref="Entities.PrinterEvent"/> reports — Homespool's own vocabulary, not any
/// protocol's. Each protocol maps its wire words into these values at its edge (for Prusa Connect
/// that is <c>PrusaEventWireMapping</c>), and the wire's own word travels alongside in
/// <see cref="Entities.PrinterEvent.WireType"/>. A value here names a fact about a printer or a
/// command; not every protocol can produce every fact, and a protocol that cannot simply never
/// emits the value. See <c>notes/domain-vocabulary.md</c> for the mapping tables.
/// </summary>
public enum PrinterEventType
{
    /// <summary>
    /// .NET-only sentinel for "not set" - never a real value.
    /// </summary>
    Undefined = 0,

    /// <summary>
    /// A command sent to the printer was accepted for processing.
    /// </summary>
    Accepted = 1,

    /// <summary>
    /// A command or request was rejected, with a reason (e.g. unknown job/file id).
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// A command's execution failed.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// A command's execution finished successfully.
    /// </summary>
    Finished = 4,

    /// <summary>
    /// Printer identity/capabilities snapshot - firmware, serial number, network, tools,
    /// storages. Sent on connect and in response to an information request.
    /// </summary>
    Info = 5,

    /// <summary>
    /// The printer's dialog/attention UI state changed (code, title, text, buttons) - not the
    /// coarse idle/printing/paused device state, which travels on every message's own
    /// <c>state</c> field instead.
    /// </summary>
    StateChanged = 6,

    /// <summary>
    /// A storage medium (SD card/USB) was removed.
    /// </summary>
    StorageEjected = 7,

    /// <summary>
    /// A storage medium (SD card/USB) was inserted.
    /// </summary>
    StorageInserted = 8,

    /// <summary>
    /// A file or folder was added, removed, or modified.
    /// </summary>
    FileChanged = 9,

    /// <summary>
    /// Metadata about a specific file or folder, in response to a file-information request.
    /// </summary>
    FileInfo = 10,

    /// <summary>
    /// Metadata about the current print job, in response to a job-information request.
    /// </summary>
    JobInfo = 11,

    /// <summary>
    /// Progress of an in-progress file transfer (size, transferred, time remaining).
    /// </summary>
    TransferInfo = 12,

    /// <summary>
    /// Bed-leveling mesh data.
    /// </summary>
    MeshBedData = 13,

    /// <summary>
    /// A file transfer ended abnormally (e.g. a validation failure), not by request.
    /// </summary>
    TransferAborted = 14,

    /// <summary>
    /// A file transfer was stopped, e.g. by user request.
    /// </summary>
    TransferStopped = 15,

    /// <summary>
    /// A file transfer completed successfully.
    /// </summary>
    TransferFinished = 16,

    /// <summary>
    /// An MMU/multi-tool slot-related event.
    /// </summary>
    SlotEvent = 17,

    /// <summary>
    /// Whether the current action/dialog can be cancelled has changed.
    /// </summary>
    CancelableChanged = 18,
}
