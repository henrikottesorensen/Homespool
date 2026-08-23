using System.Text.Json.Serialization;

namespace Homespool.Host.Cameras;

/// <summary>
/// How a camera can be watched live, decided from the codec the stream server reports.
/// </summary>
/// <remarks>
/// The wire names are part of the page contract - camera-live.js switches on them - so they are
/// pinned here rather than left to whatever the serializer would derive from the member names.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<LiveTransport>))]
public enum LiveTransport
{
    /// <summary>Never set. Reserved so a default-constructed value is not an answer.</summary>
    /// <remarks>
    /// <b>Distinct from <see cref="None"/>, which is one</b> - "there is no way to watch this
    /// camera" is something somebody established, and it must not share a slot with a field nobody
    /// wrote.
    /// </remarks>
    Undefined = 0,

    /// <summary>No live view: the codec is carried by no transport, or the camera did not answer.</summary>
    [JsonStringEnumMemberName("none")]
    None = 1,

    /// <summary>Negotiate a WebRTC session; the media travels from the sidecar to the browser directly.</summary>
    [JsonStringEnumMemberName("webrtc")]
    Webrtc = 2,

    /// <summary>Point the picture at the relayed multipart stream; the media travels through Homespool.</summary>
    [JsonStringEnumMemberName("mjpeg")]
    Mjpeg = 3,
}
