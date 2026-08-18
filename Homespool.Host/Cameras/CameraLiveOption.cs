namespace Homespool.Host.Cameras;

/// <summary>
/// Whether a camera can be watched live, as answered to a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>One field, and no reason beside it, on purpose.</b> A page that cannot offer live view simply
/// does not show the button — there is nothing for a viewer to act on, since every reason it could
/// give ("this camera sends JPEG", "nobody configured an address") is a fact about the deployment
/// rather than about anything they can do. An administrator is told, in the banner, where it can be
/// acted on.
/// </para>
/// <para>
/// A record rather than a bare boolean because it is a body: a field can be added later without
/// every caller having to change shape, where <c>true</c> on its own could not.
/// </para>
/// </remarks>
/// <param name="Available">Whether a live view may be started for this camera right now.</param>
public sealed record CameraLiveOption(bool Available);
