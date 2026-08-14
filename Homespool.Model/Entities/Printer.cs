using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

public class Printer
{
    /// <summary>
    /// Surrogate primary key, and the foreign key used by every high-volume table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A 32-bit integer rather than the <see cref="Uuid"/>, because this key is copied into
    /// <c>TelemetrySample</c>, <c>PrinterEvent</c> and their slot children — appearing both in
    /// every row and in every entry of the hot <c>(PrinterId, Timestamp)</c> index. EF maps
    /// <c>Guid</c> to <c>TEXT</c> on SQLite, so that would be 36 bytes per occurrence against
    /// 1-2 for a small varint-encoded integer. Measured at roughly +75 MB per million sample
    /// rows, with range queries about 28% slower.
    /// </para>
    /// <para>
    /// Note the usual "UUIDs fragment your table" warning does <b>not</b> apply here: it is about
    /// clustered indexes in SQL Server and InnoDB. SQLite only clusters on
    /// <c>INTEGER PRIMARY KEY</c>, and PostgreSQL stores heaps. Size is the real cost, not
    /// fragmentation — which is also why UUIDv7 would not have helped.
    /// </para>
    /// <para>
    /// 32 bits is ample: this is a self-hosted service for one to tens of printers.
    /// </para>
    /// </remarks>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Opaque public identifier, used in URLs and any future API — never as a foreign key.
    /// </summary>
    /// <remarks>
    /// Keeping this off the relational path means the cost of an opaque identifier is paid once
    /// per printer, on a table with tens of rows, instead of once per telemetry sample.
    /// </remarks>
    public Guid Uuid { get; set; }

    public PrinterType Type { get; set; }

    /// <summary>
    /// The team that owns this printer. Printers belong to teams, not users (phase-1.5 §15): this
    /// is what makes sharing a printer between people a matter of team membership rather than a
    /// schema change later.
    /// </summary>
    /// <remarks>
    /// Replaces the earlier <c>Owner</c> (a user id). Keeping both would put authority in two
    /// places — the same mistake the <c>Material</c>/<c>LoadedMaterial</c> duplication (§13) removed.
    /// An <see cref="int"/> foreign key rather than the team's own surrogate width for the same
    /// reason <see cref="Id"/> is an int: it is cheap and this is a self-hosted service.
    /// </remarks>
    public int TeamId { get; set; }

    [ForeignKey(nameof(TeamId))]
    public virtual Team? Team { get; set; }

    /// <summary>
    /// User-chosen display name. Null means the user has not customised it — resolve for display
    /// as <c>Name ?? Model ?? Uuid</c>, which is what <c>Pages/Printers/Index</c> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This said <c>Name ?? Model ?? SerialNumber</c> until 2026-07-28, naming a property that did
    /// not exist. It does now (<see cref="SerialNumber"/>), but it is still the wrong last resort:
    /// it is null until the first <c>INFO</c> arrives, whereas <see cref="Uuid"/> exists from
    /// creation and so is the only fallback that cannot itself be missing.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Deliberately not defaulted to <see cref="Model"/> at creation. Storing a copy makes
    /// "the user chose this" indistinguishable from "we defaulted it", which means the default
    /// can never be safely refreshed once the real model arrives.
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>
    /// Printer model, from the <c>printer_type</c> field of the <c>INFO</c> event.
    /// </summary>
    /// <remarks>
    /// Null until the first <c>INFO</c> event arrives. The registration handshake also carries
    /// <c>printer_type</c>, but it is deliberately not persisted: Buddy's <c>Planner::reset()</c>
    /// marks the info state dirty on every connect ("Will trigger an Info message on the next
    /// one"), so <c>INFO</c> is guaranteed on connection and re-sent whenever
    /// <c>info_fingerprint()</c> changes. Anything stored at registration would be a strictly
    /// poorer, staler copy. See AGENT-NOTES §13.
    /// </remarks>
    public string? Model { get; set; }

    /// <summary>
    /// The printer's serial number, from the <c>sn</c> field of the <c>INFO</c> event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written once and then left alone</b>, unlike <see cref="Firmware"/> and <see cref="Model"/>,
    /// which are refreshed on every <c>INFO</c>. A firmware upgrade changes the version, and an
    /// upgrade kit genuinely changes the model under one identity - but a different serial number
    /// means a different machine, which arrives with a different fingerprint and is therefore a
    /// different row. So a serial that disagrees with the stored one is not something to act on
    /// (Henrik, 2026-07-28); only a missing one is filled in.
    /// </para>
    /// <para>
    /// Null until the first <c>INFO</c> arrives. The code-exchange handshake also carries it, on
    /// <see cref="PrusaConnectRegistration.SerialNumber"/>, but that row is deleted once enrolment
    /// completes - so before this column existed the serial was captured at registration and then
    /// discarded, and a USB-provisioned printer never reported one at all.
    /// </para>
    /// </remarks>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Installed nozzle diameter in millimetres, from the top-level <c>nozzle_diameter</c> field of
    /// the <c>INFO</c> event. Null until the printer has connected once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refreshed on every <c>INFO</c>, like <see cref="Firmware"/> and <see cref="Model"/>, because
    /// people swap nozzles</b> (Henrik, 2026-07-28). It is a property of the hardware as it stands
    /// today, not of the machine's identity - which is what separates it from
    /// <see cref="SerialNumber"/>, written once and then left alone.
    /// </para>
    /// <para>
    /// <b>Single-tool only.</b> A toolchanger reports a diameter per tool, in <c>INFO</c>'s
    /// <c>tools</c> map alongside <c>high_flow</c>, <c>hardened</c> and <c>material</c>; this column
    /// holds the top-level value, which is the whole story for an MK3.5, MK4 or MINI and only part of
    /// it for an XL. Per-tool nozzle data has no home yet - the per-slot entity that exists,
    /// <see cref="PrinterLiveSlotState"/>, carries telemetry rather than capability.
    /// </para>
    /// </remarks>
    public float? NozzleDiameter { get; set; }

    /// <summary>
    /// Whether a multi-material unit is fitted and enabled, from <c>INFO</c>'s <c>mmu.enabled</c>.
    /// </summary>
    /// <remarks>
    /// <b>A plain <c>bool</c>, deliberately</b> (Henrik, 2026-07-28). The wire distinguishes three
    /// states - enabled, present-but-disabled, and firmware without MMU support at all, where the
    /// block is absent entirely - but the last two mean the same thing operationally: treat it as a
    /// regular single-material printer. Flattening them is a product decision, not an oversight.
    /// <para>
    /// Written only when the <c>mmu</c> object is actually present, so the column's <c>false</c>
    /// default carries "regular printer" and a partial <c>INFO</c> can never clear a <c>true</c>.
    /// </para>
    /// </remarks>
    public bool HasMmuEnabled { get; set; }

    /// <summary>Set by the user in the UI. No wire source, so null until they set it.</summary>
    public string? Location { get; set; }

    /// <summary>
    /// Whether this printer may be marked ready from its page, rather than only at the machine or
    /// through the API. <b>Off unless somebody turns it on</b>, which is where the safety of it lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This does not record whether a camera is attached.</b> That is already a fact
    /// <see cref="Camera.PrinterId"/> answers, and answering it twice is how two settings for one
    /// fact end up disagreeing with neither value being wrong. What this asserts is the judgement
    /// nothing can derive: <b>that somebody reading this printer's page can tell whether its print
    /// sheet is clear.</b> A camera pointed at the spool holder leaves the feed present and this
    /// false, which is the case the setting exists for.
    /// </para>
    /// <para>
    /// <b>What it guards is printing onto a finished part</b>, which firmware will do without
    /// complaint - the same property that makes the preheat control refuse to retarget a heater
    /// mid-print. Readying a printer is a person asserting the sheet is clear; the physical walk to
    /// the machine forces that person to look, and this is what replaces it.
    /// </para>
    /// <para>
    /// <b>It gates the page and not the API.</b> <c>PUT /api/v1/printers/{uuid}/command/ready</c>
    /// answers whatever this says, because writing a script is already the deliberate act the walk
    /// stood in for, and the failure this guards needs a person who did not look. So this is a policy
    /// on a button rather than an enforced boundary - re-open that if a caller ever puts a person
    /// behind the API.
    /// </para>
    /// </remarks>
    public bool RemoteReadyAllowed { get; set; }

    /// <summary>
    /// Firmware version, e.g. <c>6.4.0+11974</c>. From the <c>INFO</c> event; null until the
    /// first one arrives, and refreshed automatically when the printer is upgraded.
    /// </summary>
    public string? Firmware { get; set; }

    public PrinterStatus Status { get; set; }

    public string? LoadedMaterial { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
