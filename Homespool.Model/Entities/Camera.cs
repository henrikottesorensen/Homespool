using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Homespool.Model.Entities;

/// <summary>
/// A camera Homespool can fetch a still image from, optionally bound to a printer.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no <c>Type</c> column</b> (Henrik, 2026-08-08). A URL already states
/// its own protocol — <c>http://</c> against <c>rtsp://</c> is the scheme, sitting in the value —
/// so a discriminator beside it would be a second source of truth for a fact the URL carries, and
/// the moment the two disagree one of them is lying. The finer distinction resolves itself too: a
/// response of <c>image/jpeg</c> and one of <c>multipart/x-mixed-replace</c> need different
/// handling, but the response says which, at fetch time, with nothing to keep in step.
/// </para>
/// <para>
/// <b><see cref="SnapshotUrl"/> is therefore constrained to HTTP(S)</b>, and protocol breadth lives
/// in the sidecar instead. go2rtc ingests RTSP, ONVIF and V4L2 and re-serves them as an HTTP
/// snapshot, so an RTSP camera — including the official Buddy Camera — reaches us through the same
/// one-shaped hole as everything else, and the application needs no protocol knowledge at all. The
/// cost of that choice is real and worth stating: an <c>rtsp://</c> address cannot be entered here
/// directly, and is unusable until something in front of it speaks HTTP.
/// </para>
/// <para>
/// <b>Nothing here holds an image.</b> Frames live in memory only, keyed by camera, and are
/// discarded once stale — see the camera cache in <c>Homespool.Host</c>. A photograph of the past
/// presented as the present is the specific failure this feature exists to prevent, and a row on
/// disk is how that starts.
/// </para>
/// </remarks>
public class Camera
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 128;

    /// <summary>Maximum length of <see cref="SnapshotUrl"/>.</summary>
    /// <remarks>
    /// Generous rather than derived: no standard bounds a URL, and the values in practice are short
    /// LAN addresses with a query string (<c>http://camera:1984/api/frame.jpeg?src=coreone</c>).
    /// The cap exists so a pasted mistake fails at the edge rather than becoming a column nobody
    /// sized.
    /// </remarks>
    public const int SnapshotUrlMaxLength = 2048;

    /// <summary>
    /// Surrogate primary key.
    /// </summary>
    /// <remarks>
    /// An <see cref="int"/> for the same reason <see cref="Printer.Id"/> is one, though the argument
    /// is far weaker here: no high-volume table references a camera, so this is consistency with the
    /// rest of the schema rather than a measured saving.
    /// </remarks>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Opaque public identifier, used in URLs — never as a foreign key.
    /// </summary>
    public Guid Uuid { get; set; }

    /// <summary>
    /// User-chosen display name. Null means they did not give one; resolve for display as
    /// <c>Name ?? Uuid</c>, the same fallback rule <see cref="Printer.Name"/> documents, and for the
    /// same reason: <see cref="Uuid"/> exists from creation, so it is the only last resort that
    /// cannot itself be missing.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The address a still image is fetched from. HTTP or HTTPS only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validated on save, not merely on display — this address is fetched <b>by the server</b>, so
    /// it reaches whatever the server can reach rather than whatever the person setting it can.
    /// Loopback and link-local are refused for that reason; see the fetcher in
    /// <c>Homespool.Host</c>, which also records that DNS rebinding defeats an address check and is
    /// knowingly unhandled.
    /// </para>
    /// <para>
    /// The threat is smaller than "internet-facing" suggests and is not nothing: setting this needs
    /// <c>ManagePrinter</c>, so the actor is an authenticated team member rather than a stranger —
    /// but teams exist, and a team member using the server to probe a network they cannot otherwise
    /// reach is the realistic case.
    /// </para>
    /// </remarks>
    public string SnapshotUrl { get; set; } = string.Empty;

    /// <summary>
    /// The printer this camera watches, or null if it is not bound to one.
    /// </summary>
    /// <remarks>
    /// Optional because Connect's own model makes it optional — the SDK sends <c>printer_uuid</c> as
    /// a query parameter on <c>/c/snapshot</c> rather than a required field, which is what lets a
    /// camera exist before, or without, a printer.
    /// </remarks>
    public int? PrinterId { get; set; }

    [ForeignKey(nameof(PrinterId))]
    public virtual Printer? Printer { get; set; }

    /// <summary>
    /// The team that owns this camera. Cameras belong to teams for the same reason printers do:
    /// sharing one is then a matter of team membership rather than a schema change.
    /// </summary>
    /// <remarks>
    /// Required even when <see cref="PrinterId"/> is set, rather than inherited through the printer.
    /// An unbound camera would otherwise have no owner at all, and authorisation that works for
    /// bound cameras and not unbound ones is the kind of gap nobody notices until it is a hole.
    /// A bound camera's team is expected to match its printer's.
    /// </remarks>
    public int TeamId { get; set; }

    [ForeignKey(nameof(TeamId))]
    public virtual Team? Team { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
