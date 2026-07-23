using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrinterService.Model.Entities;

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
    /// as <c>Name ?? Model ?? SerialNumber</c>.
    /// </summary>
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
    
    /// <summary>Set by the user in the UI. No wire source, so null until they set it.</summary>
    public string? Location { get; set; }

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
