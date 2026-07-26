using System;

using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The app-facing printer read shape (Connect's <c>Printer-read</c>). Fields the phase-1.5 claim
/// flow has no data for yet - <c>networkInfo</c>, <c>prusaLink</c>, snapshots, printer icon/image -
/// are omitted rather than faked: AGENT-NOTES phase-1.5 §15 calls this "honest nulls plus
/// <c>state: UNKNOWN</c>".
/// </summary>
public class PrinterReadDTO
{
    public required Guid Uuid { get; set; }

    public string? Name { get; set; }

    public string? Location { get; set; }

    public string? Firmware { get; set; }

    public required string State { get; set; }

    public required string Material { get; set; }

    public required int TeamId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static PrinterReadDTO FromEntity(Printer printer) => new()
    {
        Uuid = printer.Uuid,
        Name = printer.Name,
        Location = printer.Location,
        Firmware = printer.Firmware,
        State = printer.Status.ToConnectState(),
        Material = printer.LoadedMaterial ?? "UNKNOWN",
        TeamId = printer.TeamId,
        CreatedAt = printer.CreatedAt,
        UpdatedAt = printer.UpdatedAt,
    };
}
