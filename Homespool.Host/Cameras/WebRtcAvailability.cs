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
    public bool CanOffer(Guid cameraUuid, string? source)
    {
        return IsConfigured && !DeclaresJpeg(source) && !_unsupported.ContainsKey(cameraUuid);
    }

    /// <summary>
    /// Formats a source can state outright, and that WebRTC cannot carry.
    /// </summary>
    /// <remarks>
    /// <c>input_format=mjpeg</c> is the one that matters, and it is not an inference about somebody's
    /// hardware: it is a string <b>Homespool wrote</b>. The camera picker states the format rather
    /// than inheriting it, because two USB cameras disagree about which format they list first and a
    /// naive caller silently gets a per-frame transcode from one of them. The upshot is that for a
    /// camera plugged into this machine, the source says what it sends, on our own authority.
    /// </remarks>
    private static readonly string[] JpegFormats = ["input_format=mjpeg", "#video=mjpeg", "#video=jpeg"];

    /// <summary>
    /// Whether a camera's source says outright that it carries JPEG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the half of the codec question that can be answered in advance</b>, and it covers
    /// exactly the cameras that would otherwise be offered a button guaranteed to fail — the USB
    /// webcams, whose source Homespool composes. A network camera's address says nothing about its
    /// codec, so those stay optimistic and are learned from the first refusal.
    /// </para>
    /// <para>
    /// <b>Deliberately only what the source states.</b> Guessing from a scheme or a path — treating
    /// <c>http://</c> or a name ending in <c>.jpg</c> as JPEG — would take a button away from cameras
    /// that work, which is the more expensive mistake: an unnecessary refusal is invisible, where an
    /// unnecessary attempt costs one press and explains itself.
    /// </para>
    /// </remarks>
    public static bool DeclaresJpeg(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        foreach (string format in JpegFormats)
        {
            if (source.Contains(format, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records that this camera's video cannot travel over WebRTC.
    /// </summary>
    public void MarkUnsupported(Guid cameraUuid)
    {
        _unsupported[cameraUuid] = 0;
    }
}
