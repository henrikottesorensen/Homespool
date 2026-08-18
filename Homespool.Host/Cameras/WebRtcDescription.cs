using System.Text.Json.Serialization;

namespace Homespool.Host.Cameras;

/// <summary>
/// One half of a WebRTC handshake — an offer on the way in, an answer on the way back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same shape in both directions and on both wires</b>: it is what a browser's
/// <c>RTCPeerConnection</c> produces, what Homespool forwards to the sidecar, and what comes back.
/// Naming it once means the endpoint is not a place where two spellings of the same thing meet.
/// </para>
/// <para>
/// The property names are stated rather than inherited, because this leaves the application in two
/// directions with different conventions either side and the one thing that must not vary is what
/// the sidecar reads.
/// </para>
/// </remarks>
/// <param name="Type">The description's kind — <c>offer</c> or <c>answer</c>.</param>
/// <param name="Sdp">The session description itself, opaque to everything here.</param>
public sealed record WebRtcDescription(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sdp")] string Sdp);

/// <summary>
/// What came of asking the stream server to answer an offer.
/// </summary>
/// <remarks>
/// <b>The three outcomes are not three flavours of failure — the middle one is a fact about the
/// camera.</b> Nothing can tell Homespool a camera's codec before somebody tries to watch it (see
/// <see cref="WebRtcAvailability"/>), so the refusal is where that is learned, and it has to be
/// distinguishable from a camera that is merely unplugged.
/// </remarks>
public enum WebRtcOfferOutcome
{
    /// <summary>Reserved so a default-constructed value is not a meaningful outcome.</summary>
    Unknown = 0,

    /// <summary>The sidecar answered, and the answer is usable.</summary>
    Answered,

    /// <summary>
    /// This camera's video cannot travel over WebRTC — it is JPEG, and WebRTC carries H.264, VP8,
    /// VP9 and AV1.
    /// </summary>
    CodecUnsupported,

    /// <summary>Something else went wrong: unreachable, refused, or not understood.</summary>
    Failed,
}

/// <summary>
/// The stream server's reply to an offer.
/// </summary>
/// <param name="Outcome">Which of the three happened.</param>
/// <param name="Sdp">The answer, present only when <paramref name="Outcome"/> is
/// <see cref="WebRtcOfferOutcome.Answered"/>.</param>
public sealed record WebRtcOffer(WebRtcOfferOutcome Outcome, string? Sdp);
