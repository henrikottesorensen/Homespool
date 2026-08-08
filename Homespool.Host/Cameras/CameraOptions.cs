namespace Homespool.Host.Cameras;

/// <summary>
/// How often a camera is asked for a frame, how long a frame stays usable, and what is refused,
/// bound from the <c>Cameras</c> configuration section.
/// </summary>
public class CameraOptions
{
    public const string SectionName = "Cameras";

    /// <summary>
    /// Base address of the go2rtc sidecar. Default <c>http://go2rtc:1984</c>.
    /// </summary>
    /// <remarks>
    /// The service name on the Compose network, not a published port - the sidecar's API has no
    /// authentication of its own, so it is reachable from inside the stack and from nowhere else.
    /// Homespool is the only thing that configures it, which is what makes that safe.
    /// </remarks>
    public string StreamServerBaseUrl { get; set; } = "http://go2rtc:1984";

    /// <summary>
    /// Shortest gap between two fetches of the same camera, in seconds. Default 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page asking for a frame is what triggers the next fetch, so without a floor a browser
    /// polling every second would drive the camera as fast as it can answer. Measured on a Pi 4:
    /// a USB camera served through go2rtc answers in about 0.55 s and an H.264 RTSP camera in
    /// 2.0-3.5 s, each costing CPU on a board that is also running the application.
    /// </para>
    /// <para>
    /// Two seconds is chosen to sit just under the slower of those, so an RTSP camera is limited by
    /// its own acquisition time rather than by this, and a fast camera is stopped from spending the
    /// board on frames nobody asked to be that fresh.
    /// </para>
    /// </remarks>
    public int RefreshFloorSeconds { get; set; } = 2;

    /// <summary>
    /// How old a frame may be and still be served, in seconds. Default 60.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Past this a frame is discarded rather than labelled.</b> Frames are only fetched while
    /// someone is looking, so a camera nobody has opened for a day would otherwise hold yesterday's
    /// image and hand it over instantly and confidently on the next request — and a day-old
    /// photograph of a clear print bed looks exactly like a current one (Henrik, 2026-08-08).
    /// </para>
    /// <para>
    /// An age caption is not protection: people look at the picture. So the cache answers "nothing
    /// current" and the page shows that it is capturing, which is honest about the one thing that
    /// matters — whether what you are seeing is now.
    /// </para>
    /// <para>
    /// The default is comfortably longer than any measured acquisition and far shorter than
    /// anything that could mislead.
    /// </para>
    /// </remarks>
    public int MaxAgeSeconds { get; set; } = 60;

    /// <summary>
    /// How long to wait for a camera to answer, in seconds. Default 15.
    /// </summary>
    /// <remarks>
    /// Generous against the measured worst case — a cold RTSP grab took 4.6 s, because an H.264
    /// client cannot decode anything until the next keyframe and this camera's GOP is about 2.25 s.
    /// Bounded all the same: a camera that stops answering must not hold a connection open
    /// indefinitely.
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Largest response accepted from a camera, in bytes. Default 4 MiB.
    /// </summary>
    /// <remarks>
    /// The same argument as <c>PrusaConnect:MaxIncomingMessageBytes</c>: an unbounded read from
    /// something we do not control is a memory hazard, whatever it claims its length is. Measured
    /// frames are 40-190 KB, so this is roughly twenty times the largest seen — generous enough
    /// that a higher-resolution camera is not silently broken by it.
    /// </remarks>
    public long MaxFrameBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>
    /// Whether to refuse camera sources pointing at loopback or link-local addresses. Default
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Homespool does not fetch a camera source itself - the sidecar does - but it decides what the
    /// sidecar is asked to reach, which is the same authority wearing a different hat. Without this,
    /// a camera address could name the application's own unpublished port, the container beside it,
    /// a router's admin page or a cloud metadata endpoint. Setting a camera needs
    /// <c>ManagePrinter</c>, so the actor is an authenticated team member rather than a stranger;
    /// the realistic case is a team member probing a network they cannot otherwise touch.
    /// </para>
    /// <para>
    /// Loopback and link-local are refused because nothing a real camera serves lives there, so the
    /// restriction costs nothing. Everything else is allowed deliberately: a private-range allowlist
    /// would be the right answer for a hosted service and the wrong one here, where reaching a LAN
    /// camera is the entire point.
    /// </para>
    /// <para>
    /// <b>Known and unhandled: DNS rebinding.</b> This is checked when a camera is saved and the
    /// sidecar connects later, so a name that resolves past the check and then elsewhere defeats it.
    /// Closing that means pinning the resolved address for the life of the stream, and that
    /// connection is not ours to make - it is a limit of the check, not an oversight in it.
    /// </para>
    /// </remarks>
    public bool RefuseLoopbackAndLinkLocal { get; set; } = true;
}
