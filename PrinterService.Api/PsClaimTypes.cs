namespace PrinterService.Api;

public static class PsClaimTypes
{
    /// <summary>
    /// The printer's surrogate key. This is the internal <c>Printer.Id</c>, not the public
    /// <c>Uuid</c> — the principal never leaves the server, and dispatch needs the value that
    /// telemetry rows are keyed by.
    /// </summary>
    public const string PrinterId = "printer-id";

    public const string Owner =  "owner";
}
