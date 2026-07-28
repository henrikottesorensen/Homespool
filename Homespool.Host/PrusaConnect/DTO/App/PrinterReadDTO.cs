using System;

using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The app-facing printer read shape (Connect's <c>Printer-read</c>). Fields the phase-1.5 claim
/// flow has no data for yet - <c>networkInfo</c>, <c>prusaLink</c>, snapshots, printer icon/image -
/// are omitted rather than faked: AGENT-NOTES phase-1.5 §15 calls this "honest nulls plus
/// <c>state: UNKNOWN</c>".
/// </summary>
/// <remarks>
/// <b><c>state</c> now comes from <see cref="PrinterLiveState"/>, not from <see cref="Printer"/>.</b>
/// The "plus <c>state: UNKNOWN</c>" above was honest when it was written - phase 1.5 had no telemetry,
/// so there was nothing else to say. It stopped being honest when phase 3 began writing live state and
/// nothing came back to revisit this mapping: <see cref="Printer.Status"/> is assigned once at
/// creation and never again, so every printer reported <c>UNKNOWN</c> forever, however busy it was.
/// A caller with no live state - a printer that has never connected - still gets <c>UNKNOWN</c>, which
/// is the case the original wording was actually describing.
/// </remarks>
public class PrinterReadDTO
{
    public required Guid Uuid { get; set; }

    public string? Name { get; set; }

    public string? Location { get; set; }

    /// <summary>
    /// Printer model, from <c>INFO</c>'s <c>printer_type</c>. Null until the printer has connected
    /// once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth exposing beyond curiosity: it is the second link in the display chain a client has to
    /// reproduce, <c>Name ?? Model ?? Uuid</c>. Without it an API consumer cannot render a printer
    /// the way the web UI does.
    /// </para>
    /// <para>
    /// <b>Named <c>printerModel</c> rather than <c>model</c> to match Connect's own <c>Printer</c>
    /// schema</b> (2026-07-28). It shipped as <c>model</c> earlier the same day and was the only one
    /// of this DTO's fields whose name diverged from the spec - the other ten already matched. The
    /// entity property stays <see cref="Printer.Model"/>; the alignment is a wire-shape concern, and
    /// <c>PrinterModel</c> on an entity called <c>Printer</c> would stutter.
    /// </para>
    /// </remarks>
    public string? PrinterModel { get; set; }

    /// <summary>The printer's serial number, from <c>INFO</c>'s <c>sn</c>. Null until it connects.</summary>
    public string? SerialNumber { get; set; }

    public string? Firmware { get; set; }

    public required string State { get; set; }

    public required string Material { get; set; }

    public required int TeamId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether the caller may see this printer. True wherever the printer was returned at
    /// all, since a caller without it gets a 404.</summary>
    public bool CanRead { get; set; }

    /// <summary>
    /// Whether the caller may send it commands - the permission <c>PrinterCommandService</c> enforces.
    /// </summary>
    /// <remarks>
    /// The useful one. Without it a client has no way to tell a printer it may only watch from one it
    /// may drive, and has to discover the difference from a 403 after trying.
    /// </remarks>
    public bool CanUse { get; set; }

    /// <summary>Whether the caller may edit or reprovision it.</summary>
    public bool CanManage { get; set; }

    /// <summary>Maps a printer, taking its reported state from the live-state row when there is one.</summary>
    /// <param name="printer">The printer row.</param>
    /// <param name="liveState">
    /// Its last-known state, or <c>null</c> when it has never connected - which is also what a caller
    /// that has not loaded it should pass. Claim responses do exactly that: a printer claimed a moment
    /// ago has said nothing yet, so <c>UNKNOWN</c> is the true answer rather than a placeholder.
    /// </param>
    public static PrinterReadDTO FromEntity(Printer printer, PrinterLiveState? liveState = null) => new()
    {
        Uuid = printer.Uuid,
        Name = printer.Name,
        Location = printer.Location,
        PrinterModel = printer.Model,
        SerialNumber = printer.SerialNumber,
        Firmware = printer.Firmware,

        // Not printer.Status - see the remarks on this class.
        State = (liveState?.Status ?? PrinterStatus.Unknown).ToConnectState(),
        Material = printer.LoadedMaterial ?? "UNKNOWN",
        TeamId = printer.TeamId,
        CreatedAt = printer.CreatedAt,
        UpdatedAt = printer.UpdatedAt,
    };

    /// <summary>
    /// Maps a printer already paired with its live state and the calling user's membership.
    /// </summary>
    /// <remarks>
    /// The permission flags are answerable only here, not on the two-argument overload: they describe
    /// the <em>caller</em>, and a mapper handed a bare <see cref="Printer"/> has not been told who is
    /// asking. Absent membership reports all three false, which is the safe reading - though the
    /// queries in <see cref="PrinterQueryService"/> always supply it, and a printer visible without a
    /// membership row is not a state this application can produce.
    /// </remarks>
    public static PrinterReadDTO FromEntity(PrinterWithState printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        PrinterReadDTO dto = FromEntity(printer.Printer, printer.LiveState);

        dto.CanRead = printer.Membership?.CanRead ?? false;
        dto.CanUse = printer.Membership?.CanUse ?? false;
        dto.CanManage = printer.Membership?.CanManage ?? false;

        return dto;
    }
}
