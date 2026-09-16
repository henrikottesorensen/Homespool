using System;
using System.ComponentModel.DataAnnotations;

using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The claim body for <c>POST /api/v1/printers/register</c>, matching Connect's mobile API
/// - <c>{name, location, code, teamId}</c>, except that the team is named by its uuid. Property names rely on
/// ASP.NET Core's default camelCase JSON policy rather than explicit <c>[JsonPropertyName]</c>,
/// since they already match the wire names as written.
/// </summary>
/// <remarks>
/// <b>The lengths are <see cref="Printer"/>'s, not this type's own</b>, because the two pages that
/// write the same columns bound them too, and a bound only some writers hold is no bound at all.
/// Nothing is stored from <c>Code</c> - it is looked up and discarded - so the request-size limit on
/// the action is the only ceiling it needs.
/// </remarks>
public class RegisterPrinterAppRequestDTO
{
    [StringLength(Printer.NameMaxLength)]
    public string? Name { get; set; }

    [StringLength(Printer.LocationMaxLength)]
    public string? Location { get; set; }

    public required string Code { get; set; }

    /// <summary>The team to put the printer in, by <see cref="Team.Uuid"/>, or null for the caller's default team.</summary>
    public Guid? TeamUuid { get; set; }
}
