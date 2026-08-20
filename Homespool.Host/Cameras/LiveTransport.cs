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
    /// <summary>No live view: the codec is carried by no transport, or the camera did not answer.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Negotiate a WebRTC session; the media travels from the sidecar to the browser directly.</summary>
    [JsonStringEnumMemberName("webrtc")]
    Webrtc,

    /// <summary>Point the picture at the relayed multipart stream; the media travels through Homespool.</summary>
    [JsonStringEnumMemberName("mjpeg")]
    Mjpeg,
}
