using System;
using System.Collections.Generic;

using Homespool.Host.Cameras;

namespace Homespool.Host.DTO;

/// <summary>A camera the caller may watch, and how it can be watched.</summary>
/// <remarks>
/// <para>
/// <b>Never the camera's address or its credentials.</b> The address is the stream server's input,
/// read by nothing a viewer uses, and choosing it is <c>ManageCamera</c>'s judgement: even without a
/// password in it, it names a LAN address or a device path. Pictures come through
/// <c>/api/v1/cameras/{uuid}/…</c>, which is the only way to them.
/// </para>
/// <para>
/// <b><see cref="Codecs"/> decides which of those routes carries a stream.</b> <c>frame</c> answers a
/// still for any camera; <c>stream.mjpeg</c> carries only a camera sending <c>JPEG</c>, since the
/// stream server does not transcode on that path; <c>webrtc</c> negotiates the rest, and the media
/// then travels from the stream server to the caller directly.
/// </para>
/// </remarks>
public class CameraReadDTO
{
    public required Guid Uuid { get; set; }

    /// <summary>The name somebody gave it, or null when nobody did.</summary>
    public string? Name { get; set; }

    public required Guid TeamUuid { get; set; }

    /// <summary>
    /// The printer this camera watches, or null when it watches none - or watches one the caller may
    /// not see, since seeing a camera and seeing a printer are separate permissions.
    /// </summary>
    public Guid? PrinterUuid { get; set; }

    /// <summary>
    /// The video codecs the camera offers, as the stream server names them. Null when the camera has
    /// not answered since the server started - usually because it is off.
    /// </summary>
    /// <remarks>
    /// Read from what is already known, never by asking the camera: a listing does not open cameras.
    /// <c>GET …/{uuid}/live</c> asks when nothing is known.
    /// </remarks>
    public IReadOnlyList<string>? Codecs { get; set; }

    /// <summary>
    /// How it can be watched live - <c>webrtc</c>, <c>mjpeg</c> or <c>none</c> - decided from
    /// <see cref="Codecs"/> as <c>…/live</c> decides it. Null exactly when <see cref="Codecs"/> is.
    /// </summary>
    public LiveTransport? Transport { get; set; }
}
