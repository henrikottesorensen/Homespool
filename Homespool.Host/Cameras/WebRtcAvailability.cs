using System;
using System.Collections.Concurrent;

namespace Homespool.Host.Cameras;

/// <summary>
/// Whether this deployment can offer live camera view, and the address that decides it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One value, three readers.</b> <see cref="WebRtcConfigurer"/> works the address out at startup
/// and writes it here; the camera endpoint refuses live view without it, and the health check
/// explains its absence to an administrator. Recomputing it in each of those would be three chances
/// to answer the same question differently — and the answer depends on a name resolving, so they
/// genuinely could.
/// </para>
/// <para>
/// <b>Written once during startup, read for the life of the process.</b> The address is a property of
/// the machine, and the point at which a moved one is noticed is a restart — the same restart that
/// has to happen anyway before the sidecar would advertise a new one.
/// </para>
/// </remarks>
public sealed class WebRtcAvailability
{
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
    /// Whether live view may be offered at all.
    /// </summary>
    /// <remarks>
    /// Without a candidate the sidecar advertises only addresses inside the Compose network, so a
    /// browser would negotiate successfully and receive nothing. Answering false here is what keeps
    /// that off the page rather than letting somebody find it.
    /// </remarks>
    public bool IsConfigured => Candidate.Length > 0;

    /// <summary>
    /// Cameras that have refused a WebRTC offer on codec grounds, so live view is not offered for
    /// them again.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _unsupported = new();

    /// <summary>
    /// Whether live view may be offered for this camera.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Optimistic, and it has to be — the codec cannot be known in advance.</b> Measured on the
    /// board 2026-08-18: <c>/api/streams</c> reports no codec at all for a stream nothing is
    /// consuming, and a still fetch closes the producer the instant the frame is served, so polling
    /// the page never makes one visible. There is no <c>/api/probe</c> in 1.9.14 either. Asking
    /// would therefore mean opening a stream to find out whether a stream can be opened.
    /// </para>
    /// <para>
    /// <b>So the negotiation is the question, and the refusal is the answer.</b> A JPEG camera is
    /// refused cleanly and permanently, which is recorded here, and the button stops appearing for
    /// it. That costs one failed attempt per such camera — visible and explained, never a silent
    /// black rectangle — against a button that would otherwise never appear for any camera at all.
    /// </para>
    /// <para>
    /// <b>Held in memory rather than on the camera's row</b>, deliberately: a different camera can be
    /// put at the same address, and a stored "this cannot do live" would outlive the hardware it was
    /// true about. Forgetting on restart is the cheapest correct answer.
    /// </para>
    /// </remarks>
    public bool CanOffer(Guid cameraUuid)
    {
        return IsConfigured && !_unsupported.ContainsKey(cameraUuid);
    }

    /// <summary>
    /// Records that this camera's video cannot travel over WebRTC.
    /// </summary>
    public void MarkUnsupported(Guid cameraUuid)
    {
        _unsupported[cameraUuid] = 0;
    }
}
