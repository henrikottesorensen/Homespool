using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The body of <c>PATCH /api/v1/printers/{uuid}</c>. The spec's
/// <c>Printer.PrinterPatchInput</c> also allows moving <c>teamId</c>; deferred - it needs a
/// permission check on both the source and destination team, which this pass doesn't do.
/// </summary>
/// <remarks>
/// <para>
/// <b>The lengths are <see cref="Printer"/>'s, not this type's own</b>, because the two pages that
/// write the same columns bound them too, and a bound only some writers hold is no bound at all.
/// Enforced by <c>[ApiController]</c>'s automatic model validation, which answers 400 before the
/// action runs - nothing downstream re-checks, and the column behind it is SQLite <c>TEXT</c>.
/// </para>
/// <para>
/// <b>A field left out of the body is left alone; a field sent as <c>null</c> is cleared</b> - merge
/// patch, as the spec's all-optional, all-nullable shape implies. A plain <c>string?</c> cannot tell
/// the two apart, so each setter also records that it ran: System.Text.Json calls a setter only for a
/// property the body names.
/// </para>
/// </remarks>
public class PrinterPatchInputDTO
{
    private string? _name;
    private string? _location;

    [StringLength(Printer.NameMaxLength)]
    [PrintableText]
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
    [PrintableText]
    public string? Location
    {
        get => _location;
        set
        {
            _location = value;
            LocationSpecified = true;
        }
    }

    /// <summary>Whether the body named <see cref="Name"/> at all, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool NameSpecified { get; private set; }

    /// <summary>Whether the body named <see cref="Location"/> at all, <c>null</c> included.</summary>
    [JsonIgnore]
    public bool LocationSpecified { get; private set; }
}
