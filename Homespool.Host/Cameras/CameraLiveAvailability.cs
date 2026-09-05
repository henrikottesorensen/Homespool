using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Cameras;

/// <summary>
/// How each camera can be watched live, decided from the codec the stream server reports - plus the
/// WebRTC media address that half of the answer depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The codec is asked for, not guessed.</b> The stream server answers an RTSP DESCRIBE with the
/// camera's actual codecs whether or not anything is watching (see
/// <see cref="Go2RtcClient.ProbeCodecsAsync"/>) - which retired this class's previous design of
/// offering optimistically and learning from refusals. A camera whose codec no transport carries
/// simply gets no button, and the two silent-failure cells that design guarded against cannot be
/// reached at all.
/// </para>
/// <para>
/// <b>Answers are remembered in memory, and only definite ones.</b> A codec is a property of the
/// hardware, so one probe per camera per process is enough - but a probe that gets no answer means
/// "the camera is off", not "the camera cannot", and is asked again on the next page. Held in memory
/// rather than on the camera's row, deliberately: a different camera can be put at the same address,
/// and a stored answer would outlive the hardware it was true about. A source edit calls
/// <see cref="Forget"/>; a restart forgets everything.
/// </para>
/// <para>
/// <b>The candidate address is written once during startup</b> by <see cref="WebRtcConfigurer"/> and
/// read for the life of the process - by the transport decision here, and by the health check that
/// explains its absence to an administrator. Recomputing it in each reader would be several chances
/// to answer the same question differently, and the answer depends on a name resolving, so they
/// genuinely could.
/// </para>
/// </remarks>
public sealed class CameraLiveAvailability
{
    /// <summary>
    /// The one codec the MJPEG stream carries. The stream server does not transcode on that path -
    /// measured 2026-08-19, when an H.264 camera answered its multipart request with 200 and then
    /// silence.
    /// </summary>
    private const string JpegCodec = "JPEG";

    /// <summary>
    /// The video codecs the stream server's WebRTC consumer carries, from its own source
    /// (<c>pkg/webrtc</c>, 1.9.14). Whether a given browser then plays one is negotiated per
    /// session - H.265 plays in Safari and not in Firefox - and a mismatch fails visibly at the
    /// offer exchange, which is the acceptable failure shape.
    /// </summary>
    private static readonly HashSet<string> WebRtcCodecs =
        new(StringComparer.OrdinalIgnoreCase) { "H264", "H265", "VP8", "VP9", "AV1" };

    private readonly ICameraCodecProbe _probe;

    /// <summary>Codecs by camera - only cameras that have actually answered a probe.</summary>
    private readonly ConcurrentDictionary<Guid, IReadOnlySet<string>> _codecs = new();

    public CameraLiveAvailability(ICameraCodecProbe probe)
    {
        _probe = probe;
    }

    /// <summary>
    /// The address a browser is told to send media to, or empty when none could be worked out.
    /// </summary>
    /// <remarks>
    /// <b>Written by <see cref="WebRtcConfigurer"/> and nothing else.</b> The setter is not narrowed
    /// beyond that because the only way to say so would be to make the type unbuildable in a test,
    /// which would cost more than the rule is worth — writing this from anywhere else would put a
    /// value here that the sidecar was never told, and the symptom would be a live-view button that
    /// negotiates against an address nobody is listening on.
    /// </remarks>
    public string Candidate { get; set; } = string.Empty;

    /// <summary>
    /// Whether the configured printer host resolved to nothing but loopback when the candidate was
    /// worked out — the one cause of an empty <see cref="Candidate"/> that has a named fix.
    /// </summary>
    /// <remarks>
    /// Written beside <see cref="Candidate"/> by the same configurer, so the health check can say
    /// what happened rather than re-resolving and possibly reaching a different answer.
    /// <see cref="Certificates.PrinterCertificateNames.ResolvesOnlyToLoopback"/> describes the case.
    /// </remarks>
    public bool ConfiguredHostResolvesOnlyToLoopback { get; set; }

    /// <summary>
    /// Whether WebRTC live view may be offered at all.
    /// </summary>
    /// <remarks>
    /// Without a candidate the sidecar advertises only addresses inside the Compose network, so a
    /// browser would negotiate successfully and receive nothing. Answering false here is what keeps
    /// that off the page rather than letting somebody find it.
    /// </remarks>
    public bool IsConfigured => Candidate.Length > 0;

    /// <summary>
    /// How this camera can be watched live right now.
    /// </summary>
    /// <remarks>
    /// <see cref="LiveTransport.None"/> covers three honest cases the page treats alike: the codec
    /// is carried by no transport, WebRTC would carry it but no candidate address is configured, and
    /// the camera did not answer the probe - you cannot watch a camera that is off, so "no button
    /// until it answers" is the truthful rendering of all three.
    /// </remarks>
    public async Task<LiveTransport> HowToWatchAsync(Guid cameraUuid, CancellationToken cancellationToken)
    {
        if (!_codecs.TryGetValue(cameraUuid, out IReadOnlySet<string>? codecs))
        {
            codecs = await _probe.ProbeCodecsAsync(cameraUuid, cancellationToken).ConfigureAwait(false);

            if (codecs is null)
            {
                return LiveTransport.None;
            }

            _codecs.TryAdd(cameraUuid, codecs);
        }

        if (codecs.Contains(JpegCodec))
        {
            return LiveTransport.Mjpeg;
        }

        if (IsConfigured && WebRtcCodecs.Overlaps(codecs))
        {
            return LiveTransport.Webrtc;
        }

        return LiveTransport.None;
    }

    /// <summary>
    /// Drops what is remembered about this camera. Called when its source changes - the codec that
    /// was probed belongs to the hardware at the old address.
    /// </summary>
    public void Forget(Guid cameraUuid)
    {
        _codecs.TryRemove(cameraUuid, out _);
    }
}
