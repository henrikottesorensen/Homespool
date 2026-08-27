using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect.DTO.App;

/// <summary>
/// The app-facing printer read shape (Connect's <c>Printer-read</c>). Fields the phase-1.5 claim
/// flow has no data for yet - <c>networkInfo</c>, <c>prusaLink</c>, snapshots, printer icon/image -
/// are omitted rather than faked - honest nulls plus <c>state: UNKNOWN</c>.
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
    /// <b>Named <c>printerType</c>, which took two goes to get right</b> (2026-07-28). It shipped as
    /// <c>model</c>, was renamed <c>printerModel</c> to match Connect's schema, and that was the wrong
    /// field of three: their examples are <c>printerType: "1.4.0"</c>, <c>printerTypeName: "MK4"</c>
    /// and <c>printerModel: "MK4SISMMU3"</c>. Ours is <c>1.3.5</c> - a code, straight from
    /// <c>INFO</c>'s <c>printer_type</c> - so it is their <c>printerType</c>. The other two are
    /// omitted rather than faked: <c>printerTypeName</c> needs a code-to-name lookup we do not have,
    /// and <c>printerModel</c> needs a SKU the printer never sends.
    /// </para>
    /// <para>
    /// <b>Do not confuse this with <see cref="Homespool.Model.PrinterType"/></b>, the entity's own
    /// enum. That one is the <em>protocol the printer speaks</em> - the seam for supporting something
    /// other than Prusa Connect - and it is deliberately not on the wire, because publishing it as
    /// <c>printerType</c> would put a transport name where every client expects a hardware code. Two
    /// unrelated meanings, one name, and only one of them belongs in this DTO.
    /// </para>
    /// </remarks>
    public string? PrinterType { get; set; }

    /// <summary>
    /// The human name for <see cref="PrinterType"/> - <c>MK3.5</c> for <c>1.3.5</c>. Null for a
    /// printer that has not connected, and for one newer than the generated table.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored: <see cref="PrinterModelNames"/> is generated from firmware's own
    /// <c>printer_model_info</c>, so this needs no column and cannot disagree with
    /// <see cref="PrinterType"/>.
    /// <para>
    /// Connect's third field, <c>printerModel</c> (<c>MK4SISMMU3</c>), is still omitted: it comes
    /// from firmware's separate <c>printer_model_mmu_variant</c> table, which is keyed by model plus
    /// MMU state rather than by the version triple - which is why the spec carries
    /// <c>hasMmuEnabled</c> alongside it. Not derivable from what we receive.
    /// </para>
    /// </remarks>
    public string? PrinterTypeName { get; set; }

    /// <summary>
    /// Whether a multi-material unit is fitted and enabled. False for a printer that has never
    /// connected, and for one whose firmware has no MMU support - see <see cref="Printer.HasMmuEnabled"/>
    /// for why those collapse into one value.
    /// </summary>
    public bool HasMmuEnabled { get; set; }

    /// <summary>The printer's serial number, from <c>INFO</c>'s <c>sn</c>. Null until it connects.</summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Installed nozzle diameter in millimetres, from <c>INFO</c>. Null until the printer connects,
    /// and refreshed whenever someone swaps a nozzle.
    /// </summary>
    /// <remarks>
    /// <b><see cref="float"/>, and it has to stay one.</b> SQLite has no 4-byte float, so the stored
    /// value is the double widening of what the printer sent - a real MK3.5 reporting 0.4 is held as
    /// 0.400000005960464. Narrowing back to <see cref="float"/> here is what makes
    /// <c>System.Text.Json</c> emit <c>0.4</c>; typed <c>double?</c> this field would put
    /// <c>0.40000000596046448</c> on the wire.
    /// </remarks>
    public float? NozzleDiameter { get; set; }

    public string? Firmware { get; set; }

    public required string State { get; set; }

    public required string Material { get; set; }

    public required int TeamId { get; set; }

    /// <summary>
    /// The owning team's name, or null when nobody has named it - which is every team created by
    /// default, since <see cref="Team.Name"/> is deliberately not seeded at creation.
    /// </summary>
    /// <remarks>
    /// <b>Passed through rather than resolved.</b> Two fallbacks already exist and disagree:
    /// <see cref="Team.Name"/>'s own remarks describe <c>Name ?? "&lt;creator&gt;'s team"</c>, while
    /// <c>Pages/Printers/Index</c> renders <c>Name ?? "Team #{id}"</c>. Inventing a third here would
    /// make the API a third opinion on a display question. A client holds <see cref="TeamId"/> and can
    /// render whichever it prefers; what it cannot do is recover a real name we declined to send.
    /// </remarks>
    public string? TeamName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// What the caller may do to this printer - <c>Capability</c> names, as the membership grants
    /// them.
    /// </summary>
    /// <remarks>
    /// <b>The useful field, and the reason it is a list rather than flags.</b> Without it a client has
    /// no way to tell a printer it may only watch from one it may drive, and has to discover the
    /// difference from a 403 after trying. It was three booleans until 2026-08-14, which was Connect's
    /// mobile-API shape; <c>ViewPrinter</c> is present on anything returned at all, since a caller
    /// without it gets a 404.
    /// </remarks>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>Maps a printer, taking its reported state from the live-state row when there is one.</summary>
    /// <param name="printer">The printer row.</param>
    /// <param name="liveState">
    /// Its last-known state, or <c>null</c> when it has never connected - which is also what a caller
    /// that has not loaded it should pass. Claim responses do exactly that: a printer claimed a moment
    /// ago has said nothing yet, so <c>UNKNOWN</c> is the true answer rather than a placeholder.
    /// </param>
    public static PrinterReadDTO FromEntity(Printer printer, PrinterLiveState? liveState = null)
    {
        return new()
        {
            Uuid = printer.Uuid,
            Name = printer.Name,
            Location = printer.Location,
            PrinterType = printer.Model,
            PrinterTypeName = PrinterModelNames.ForPrinterType(printer.Model),
            SerialNumber = printer.SerialNumber,
            HasMmuEnabled = printer.HasMmuEnabled,
            NozzleDiameter = printer.NozzleDiameter,
            Firmware = printer.Firmware,

            // Not printer.Status - see the remarks on this class.
            State = (liveState?.Status ?? PrinterStatus.Unknown).ToConnectState(),
            Material = printer.LoadedMaterial ?? "UNKNOWN",
            TeamId = printer.TeamId,
            CreatedAt = printer.CreatedAt,
            UpdatedAt = printer.UpdatedAt,
        };
    }

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

        dto.TeamName = printer.Team?.Name;
        dto.Capabilities = CapabilitySet.Parse(printer.Membership?.Capabilities)
                                        .Granted
                                        .OrderBy(capability => capability)
                                        .Select(capability => capability.ToString())
                                        .ToList();

        return dto;
    }
}
