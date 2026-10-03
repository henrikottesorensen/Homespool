using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

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
    private string? _name;
    private string? _location;

    /// <summary>
    /// Whether the body carried <c>name</c> at all. The JSON binder only calls the setter for a
    /// property that is present, so this tells an omitted field (leave it) from an explicit
    /// <c>null</c> (clear it).
    /// </summary>
    [JsonIgnore]
    public bool NameSpecified { get; private set; }

    [JsonIgnore]
    public bool LocationSpecified { get; private set; }

    [StringLength(Printer.NameMaxLength)]
    public string? Name
    {
        get => _name;
        set
        {
            _name = value;
            NameSpecified = true;
        }
    }

    [StringLength(Printer.LocationMaxLength)]
    public string? Location
    {
        get => _location;
        set
        {
            _location = value;
            LocationSpecified = true;
        }
    }
}
