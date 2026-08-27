namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The claim body for <c>POST /api/v1/printers/register</c>, matching Connect's mobile API
/// - <c>{name, location, code, teamId}</c>. Property names rely on
/// ASP.NET Core's default camelCase JSON policy rather than explicit <c>[JsonPropertyName]</c>,
/// since they already match the wire names as written.
/// </summary>
public class RegisterPrinterAppRequestDTO
{
    public string? Name { get; set; }

    public string? Location { get; set; }

    public required string Code { get; set; }

    public int? TeamId { get; set; }
}
