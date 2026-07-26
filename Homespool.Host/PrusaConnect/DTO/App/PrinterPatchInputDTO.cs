namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The body of <c>PATCH /api/v1/printers/{uuid}</c>. The spec's
/// <c>Printer.PrinterPatchInput</c> also allows moving <c>teamId</c>; deferred - it needs a
/// permission check on both the source and destination team, which this pass doesn't do.
/// </summary>
public class PrinterPatchInputDTO
{
    public string? Name { get; set; }

    public string? Location { get; set; }
}
