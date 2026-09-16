using System.ComponentModel.DataAnnotations;

using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The body of <c>PATCH /api/v1/printers/{uuid}</c>. The spec's
/// <c>Printer.PrinterPatchInput</c> also allows moving <c>teamId</c>; deferred - it needs a
/// permission check on both the source and destination team, which this pass doesn't do.
/// </summary>
/// <remarks>
/// <b>The lengths are <see cref="Printer"/>'s, not this type's own</b>, because the two pages that
/// write the same columns bound them too, and a bound only some writers hold is no bound at all.
/// Enforced by <c>[ApiController]</c>'s automatic model validation, which answers 400 before the
/// action runs - nothing downstream re-checks, and the column behind it is SQLite <c>TEXT</c>.
/// </remarks>
public class PrinterPatchInputDTO
{
    [StringLength(Printer.NameMaxLength)]
    public string? Name { get; set; }

    [StringLength(Printer.LocationMaxLength)]
    public string? Location { get; set; }
}
