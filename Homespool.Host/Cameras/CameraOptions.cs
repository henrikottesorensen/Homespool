using System;

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
    /// Username for the stream server's API, or empty for none. Default empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Required, not optional</b> — see <see cref="IsAuthenticated"/> for what changes without it,
    /// and why that reverses the earlier reading of this setting.
    /// </para>
    /// <para>
    /// <b>Not the access control, all the same.</b> go2rtc's authentication is a single credential
    /// for its whole API - it has no notion of which streams a caller may see - so it cannot express
    /// "this account may view the printer camera and not the workshop one". That remains
    /// <c>CameraAccessService</c>'s job, and every viewing path is still proxied by Homespool.
    /// </para>
    /// <para>
    /// <b>Passed to the sidecar on its command line, never written into its config file</b>
    /// (<c>compose.yaml</c>). That matters twice: the config is rewritten by the stream registration
    /// path, so a credential living there would have to survive every merge; and writing it there in
    /// the first place would require authenticating to a server that is not yet configured.
    /// </para>
    /// </remarks>
    public string ApiUsername { get; set; } = string.Empty;

    /// <summary>
    /// Password for the stream server's API. Both halves must be set for cameras to work at all.
    /// </summary>
    public string ApiPassword { get; set; } = string.Empty;

    /// <summary>
    /// Whether the sidecar has a credential, and therefore whether cameras work at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Decided by configuration alone, never by asking the sidecar</b> — the same rule as
    /// <see cref="Services.SmtpOptions.IsConfigured"/>, and for the same reason: a service being
    /// briefly unreachable must not quietly change what this deployment permits.
    /// </para>
    /// <para>
    /// <b>Both halves or neither, and that is measured rather than assumed</b> (2026-08-09): a
    /// username with an empty password turns go2rtc's authentication <i>on</i> with an empty key and
    /// answers 401 to everything, Homespool included. So half a credential is worse than none, and
    /// this predicate is the one place that judgement is made.
    /// </para>
    /// <para>
    /// <b>Why an absent credential now stops cameras rather than merely omitting a header</b>
    /// (2026-08-17). It used to be defence in depth: the sidecar's port is not published, so an empty
    /// pair was the arrangement every deployment already had. That reasoning covered the outside and
    /// missed the inside. go2rtc's API takes an ad-hoc source in <c>src</c> and supports <c>exec:</c>,
    /// so a camera source naming the sidecar's own API is a way for a team member to run a command
    /// inside a container that mounts <c>/dev</c> and can reach the printer listener directly. Its
    /// own credential is what refuses that self-fetch — so with no credential there is nothing between
    /// the two, and the safe answer is to decline to use the sidecar at all rather than to use it
    /// unauthenticated. <see cref="CameraSourcePolicy"/> is the other half of that argument.
    /// </para>
    /// </remarks>
    public bool IsAuthenticated => !string.IsNullOrEmpty(ApiUsername) && !string.IsNullOrEmpty(ApiPassword);

    /// <summary>
    /// Whether this credential can reach the sidecar unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two halves travel by different roads and the roads disagree.</b> Compose hands this
    /// process the value as a plain YAML environment variable, and hands the sidecar the same value
    /// interpolated into a <i>JSON</i> string on its command line. A <c>"</c> makes that JSON
    /// invalid; a <c>\</c> is read as a JSON escape, so <c>has\back</c> arrives at the sidecar as
    /// <c>has</c>, backspace, <c>ack</c> while arriving here intact.
    /// </para>
    /// <para>
    /// <b>The backslash case is why this is checked rather than documented alone.</b> It fails
    /// silently: both halves look configured, this deployment believes it has a credential, and every
    /// camera answers 401 with nothing saying why. Base64 output contains neither character, which is
    /// why <c>openssl rand -base64 24</c> is what the documentation recommends — but a hand-edited
    /// <c>.env</c> never passes through the wizard that would have said so.
    /// </para>
    /// <para>
    /// This is a judgement about configuration, not a refusal: a credential that cannot survive the
    /// trip leaves cameras as broken as no credential would, so refusing here would add nothing that
    /// the sidecar's own 401 does not already do. What it buys is the diagnosis.
    /// </para>
    /// </remarks>
    public bool CredentialSurvivesTransport =>
        !ApiUsername.Contains('"', StringComparison.Ordinal)
        && !ApiUsername.Contains('\\', StringComparison.Ordinal)
        && !ApiPassword.Contains('"', StringComparison.Ordinal)
        && !ApiPassword.Contains('\\', StringComparison.Ordinal);

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
