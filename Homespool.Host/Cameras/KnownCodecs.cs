using System.Collections.Generic;

namespace Homespool.Host.Cameras;

/// <summary>A camera's video codecs as it last answered them, and the live transport they allow.</summary>
/// <param name="Codecs">The video codecs the camera offers, as the stream server names them - <c>H264</c>, <c>JPEG</c>.</param>
/// <param name="Transport">How it can be watched live, decided from those codecs by the same rule a page is given.</param>
public sealed record KnownCodecs(IReadOnlySet<string> Codecs, LiveTransport Transport);
