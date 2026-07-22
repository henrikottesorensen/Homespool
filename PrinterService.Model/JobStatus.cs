namespace PrinterService.Model;

public enum JobStatus
{
    Undefined = 0,
    Printing,
    Paused,
    Finished,
    Error,
    Stopped,
};
