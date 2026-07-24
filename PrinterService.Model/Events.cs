namespace PrinterService.Model;

public enum Events
{
    /// <summary>
    /// .NET-only sentinel for "not set" - never a real value on the wire.
    /// </summary>
    Undefined = 0,

    /// <summary>
    /// A command sent to the printer was accepted for processing.
    /// </summary>
    Accepted,

    /// <summary>
    /// A command or request was rejected, with a reason (e.g. unknown job/file id).
    /// </summary>
    Rejected,

    /// <summary>
    /// A command's execution failed.
    /// </summary>
    Failed,

    /// <summary>
    /// A command's execution finished successfully.
    /// </summary>
    Finished,

    /// <summary>
    /// Printer identity/capabilities snapshot - firmware, serial number, network, tools,
    /// storages. Sent on connect and in response to a SEND_INFO command.
    /// </summary>
    Info,

    /// <summary>
    /// The printer's dialog/attention UI state changed (code, title, text, buttons) - not the
    /// coarse idle/printing/paused device state, which travels on every message's own
    /// <c>state</c> field instead.
    /// </summary>
    StateChanged,

    /// <summary>
    /// A storage medium (SD card/USB) was removed. In the Connect SDK's Event enum; Buddy
    /// firmware's own EventType enum (planner.hpp) has no such member.
    /// </summary>
    MediumEjected,

    /// <summary>
    /// A storage medium (SD card/USB) was inserted. Same caveat as <see cref="MediumEjected"/>.
    /// </summary>
    MediumInserted,

    /// <summary>
    /// A file or folder was added, removed, or modified.
    /// </summary>
    FileChanged,

    /// <summary>
    /// Metadata about a specific file or folder, in response to a SEND_FILE_INFO command.
    /// </summary>
    FileInfo,

    /// <summary>
    /// Metadata about the current print job, in response to a SEND_JOB_INFO command.
    /// </summary>
    JobInfo,

    /// <summary>
    /// Progress of an in-progress file transfer (size, transferred, time remaining).
    /// </summary>
    TransferInfo,

    /// <summary>
    /// Bed-leveling mesh data. In the Connect SDK's Event enum; Buddy firmware's own EventType
    /// enum (planner.hpp) has no such member.
    /// </summary>
    MeshBedData,

    /// <summary>
    /// A file transfer ended abnormally (e.g. a validation failure), not by request.
    /// </summary>
    TransferAborted,

    /// <summary>
    /// A file transfer was stopped, e.g. by user request.
    /// </summary>
    TransferStopped,

    /// <summary>
    /// A file transfer completed successfully.
    /// </summary>
    TransferFinished,

    /// <summary>
    /// An MMU/multi-tool slot-related event. In the Connect SDK's Event enum; Buddy firmware's
    /// own EventType enum (planner.hpp) has no such member.
    /// </summary>
    SlotEvent,

    /// <summary>
    /// Whether the current action/dialog can be canceled has changed. The reverse case: sent by
    /// Buddy firmware's EventType enum (planner.hpp), but absent from the Connect SDK's own Event
    /// enum as of its latest checked-out release - the SDK genuinely hasn't caught up to this
    /// firmware-side addition, not a deliberate protocol omission.
    /// </summary>
    CancelableChanged,
}
