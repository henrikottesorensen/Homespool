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
/// <b>Homespool owns the stream server's configuration</b> (Henrik, 2026-08-08). A camera is
/// described once, here, and registered with go2rtc from this row - rather than configured in
/// go2rtc by hand and then described again in Homespool, which would be one camera in two places.
/// go2rtc ingests RTSP, ONVIF and V4L2 and re-serves them all as an HTTP snapshot, so the
/// application needs no protocol knowledge at all, including for the official Buddy Camera.
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

    /// <summary>Maximum length of <see cref="Source"/>.</summary>
    /// <remarks>
    /// Generous rather than derived: no standard bounds a URL, and the values in practice are short
    /// LAN addresses with a query string (<c>http://go2rtc:1984/api/frame.jpeg?src=coreone</c>).
    /// The cap exists so a pasted mistake fails at the edge rather than becoming a column nobody
    /// sized.
    /// </remarks>
    public const int SourceMaxLength = 2048;

    /// <summary>
    /// Maximum length of <see cref="Resolution"/>. Enough for <c>2592x1944</c> and a good deal more;
    /// the value is validated against what the camera reports rather than by this.
    /// </summary>
    public const int ResolutionMaxLength = 16;

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
    /// Where the camera's video comes from, in the stream server's own vocabulary: an
    /// <c>rtsp://</c> address, an <c>http://</c> snapshot address, or
    /// <c>ffmpeg:device?video=/dev/v4l/by-id/...</c> for a camera plugged into this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opaque to Homespool on purpose.</b> The stream server interprets it, which is what keeps
    /// this schema free of a <c>Type</c> column: a camera's protocol is stated by its own address
    /// and never restated beside it.
    /// </para>
    /// <para>
    /// <b>Not the address a frame is read from.</b> That is derived from the stream server's base
    /// address and this camera's <see cref="Uuid"/>, and is deliberately not stored - a snapshot
    /// address is a function of two things already here, so persisting it would only create
    /// something able to disagree with both.
    /// </para>
    /// <para>
    /// Checked before it is handed over, because Homespool decides what the sidecar is asked to
    /// reach even though it does not make the connection itself. See <c>CameraSourcePolicy</c>.
    /// </para>
    /// </remarks>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The capture size asked of a camera attached to this machine, as <c>WIDTHxHEIGHT</c>, or
    /// <see langword="null"/> to take whatever the camera offers by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stored beside <see cref="Source"/> rather than read out of it</b>, although the source
    /// string carries the same number in its <c>video_size</c> parameter. The source is composed
    /// from this, never the other way round: a picker that had to parse its own output back in
    /// order to show what is selected is one regular expression away from disagreeing with the
    /// thing it configured.
    /// </para>
    /// <para>
    /// <b>Null is a real answer and the default one.</b> It means no <c>video_size</c> is stated at
    /// all, so the camera and ffmpeg settle it between them — which is the honest thing to offer
    /// somebody who has no opinion, and avoids Homespool naming a size no particular camera is
    /// obliged to support.
    /// </para>
    /// <para>
    /// <b>Only meaningful for an attached camera.</b> A network camera's resolution is configured on
    /// the camera, by whichever of a dozen mechanisms its firmware provides, and nothing here can
    /// or should reach it - so this stays null for those and the picker does not offer it.
    /// </para>
    /// </remarks>
    [MaxLength(ResolutionMaxLength)]
    public string? Resolution { get; set; }

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
