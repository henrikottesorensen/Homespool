namespace Homespool.Host.Cameras;

/// <summary>
/// Whether a camera can be watched live, and how, as answered to a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport but no reason, on purpose.</b> A page that cannot offer live view simply does
/// not show the button — there is nothing for a viewer to act on, since every reason it could give
/// ("this camera's codec has no transport", "nobody configured an address") is a fact about the
/// deployment rather than about anything they can do. An administrator is told, in the banner,
/// where it can be acted on.
/// </para>
/// <para>
/// A record rather than a bare boolean because it is a body: a field can be added later without
/// every caller having to change shape, where <c>true</c> on its own could not.
/// </para>
/// </remarks>
/// <param name="Available">Whether a live view may be started for this camera right now.</param>
/// <param name="Transport">
/// How the page should watch when <paramref name="Available"/> — <see cref="LiveTransport.Webrtc"/>
/// negotiates an offer, <see cref="LiveTransport.Mjpeg"/> points the picture at the relayed
/// multipart stream. <see langword="null"/> when not available.
/// </param>
public sealed record CameraLiveOption(bool Available, LiveTransport? Transport = null);
