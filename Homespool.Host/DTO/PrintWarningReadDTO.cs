namespace Homespool.Host.DTO;

/// <summary>Something wrong between a file and the printer it was queued on.</summary>
public class PrintWarningReadDTO
{
    /// <summary>
    /// <c>Warn</c> when the print will still run, <c>Hold</c> when the queue will stop at it until the
    /// printer changes.
    /// </summary>
    public required string Severity { get; set; }

    /// <summary>A sentence for a person, in the request's language.</summary>
    public required string Message { get; set; }
}
