using System;
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
/// and a stored answer would outlive the hardware it was true about. <see cref="CameraStreamSync"/>
/// calls <see cref="Forget"/> once the stream server holds a different source; a restart forgets
/// everything.
/// </para>
/// <para>
/// <b>One probe per camera at a time, and one that straddles a <see cref="Forget"/> is not
/// remembered.</b> Every ask that finds no answer waits on the same probe, so a camera that is off
/// costs the stream server one attempt to open it however many pages ask at once. A probe sent
/// before the stream server's source changed can answer after, with the old hardware's codecs:
/// <see cref="Forget"/> drops it from the waiting list, the asks already waiting still get its answer,
/// and nothing remembers it.
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

    /// <summary>Guards <see cref="_codecs"/> and <see cref="_probes"/>, which change together.</summary>
    private readonly Lock _lock = new();

    /// <summary>Codecs by camera - only cameras that have actually answered a probe.</summary>
    private readonly Dictionary<Guid, IReadOnlySet<string>> _codecs = [];

    /// <summary>The probe each camera's asks are waiting on, while one is running.</summary>
    private readonly Dictionary<Guid, Task<IReadOnlySet<string>?>> _probes = [];

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
    /// The STUN choice the stream server has confirmed it holds, or <see langword="null"/> before it
    /// has confirmed one.
    /// </summary>
    /// <remarks>
    /// <b>Written by <see cref="WebRtcSidecarWriter"/>, and only once the sidecar agrees</b> - its
    /// configuration already matched, or was written and the server restarted. What was saved is the
    /// options' business; this is what the sidecar is doing, which is what the live-view page reports
    /// and what <see cref="WebRtcStunApplier"/> compares a saved choice against.
    /// </remarks>
    public WebRtcStunChoice? StunInForce { get; set; }

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
        IReadOnlySet<string>? codecs = await AnswerOrProbe(cameraUuid).WaitAsync(cancellationToken)
                                                                      .ConfigureAwait(false);

        return codecs is null ? LiveTransport.None : TransportFor(codecs);
    }

    /// <summary>
    /// The remembered codecs, the probe already running for this camera, or a probe started now.
    /// </summary>
    private Task<IReadOnlySet<string>?> AnswerOrProbe(Guid cameraUuid)
    {
        TaskCompletionSource<IReadOnlySet<string>?> asking;

        lock (_lock)
        {
            if (_codecs.TryGetValue(cameraUuid, out IReadOnlySet<string>? codecs))
            {
                return Task.FromResult<IReadOnlySet<string>?>(codecs);
            }

            if (_probes.TryGetValue(cameraUuid, out Task<IReadOnlySet<string>?>? running))
            {
                return running;
            }

            asking = new TaskCompletionSource<IReadOnlySet<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _probes[cameraUuid] = asking.Task;
        }

        _ = ProbeAsync(cameraUuid, asking);

        return asking.Task;
    }

    /// <summary>
    /// Asks the stream server for this camera's codecs, on behalf of every ask waiting on
    /// <paramref name="asking"/>.
    /// </summary>
    /// <remarks>
    /// <b>On no caller's token</b>, because every ask for this camera is waiting on it and one viewer
    /// leaving must not cancel it for the others. <see cref="Go2RtcClient.ProbeCodecsAsync"/> has a
    /// deadline of its own, so it still ends.
    /// </remarks>
    private async Task ProbeAsync(Guid cameraUuid, TaskCompletionSource<IReadOnlySet<string>?> asking)
    {
        IReadOnlySet<string>? codecs;

        try
        {
            codecs = await _probe.ProbeCodecsAsync(cameraUuid, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Settle(cameraUuid, asking.Task, codecs: null);
            asking.SetException(e);
            return;
        }

        Settle(cameraUuid, asking.Task, codecs);
        asking.SetResult(codecs);
    }

    /// <summary>
    /// Ends this camera's probe and remembers a definite answer - unless <see cref="Forget"/> dropped
    /// the probe while it ran, in which case the answer may be the previous source's.
    /// </summary>
    private void Settle(Guid cameraUuid, Task<IReadOnlySet<string>?> probe, IReadOnlySet<string>? codecs)
    {
        lock (_lock)
        {
            if (!_probes.TryGetValue(cameraUuid, out Task<IReadOnlySet<string>?>? running) || running != probe)
            {
                return;
            }

            _probes.Remove(cameraUuid);

            if (codecs is not null)
            {
                _codecs[cameraUuid] = codecs;
            }
        }
    }

    /// <summary>
    /// What is already known about this camera's codecs, without asking it - or null when nothing is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For a listing, which must not open cameras.</b> <see cref="HowToWatchAsync"/> probes on a
    /// miss, and a list that did so would open every camera it names and wait out the deadline of each
    /// one that is off.
    /// </para>
    /// <para>
    /// <b>A miss is rare, and it means the camera has not answered since this process started.</b> The
    /// memo is filled at startup by <see cref="CameraStreamReconciler"/> and after every save that
    /// proves the camera is on, so what is left is a camera that was off at both moments.
    /// </para>
    /// </remarks>
    public KnownCodecs? Remembered(Guid cameraUuid)
    {
        IReadOnlySet<string>? codecs;

        lock (_lock)
        {
            codecs = _codecs.GetValueOrDefault(cameraUuid);
        }

        return codecs is null ? null : new KnownCodecs(codecs, TransportFor(codecs));
    }

    private LiveTransport TransportFor(IReadOnlySet<string> codecs)
    {
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
    /// Drops what is remembered about this camera, and the probe running for it if there is one.
    /// Called once the stream server's source for it has changed - the codec that was probed belongs
    /// to the hardware at the old address.
    /// </summary>
    public void Forget(Guid cameraUuid)
    {
        lock (_lock)
        {
            _codecs.Remove(cameraUuid);
            _probes.Remove(cameraUuid);
        }
    }
}
