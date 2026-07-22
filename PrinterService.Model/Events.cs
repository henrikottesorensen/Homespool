namespace PrinterService.Model;

public enum Events
{
    Undefined = 0,
    Accepted,
    Rejected,
    Failed,
    Finished,
    Info,
    StateChanged,
    MediumEjected,
    MediumInserted,
    FileChanged,
    FileInfo,
    JobInfo,
    TransferInfo,
    MeshBedData,
    TransferAborted,
    TransferStopped,
    TransferFinished,
    SlotEvent,
}
