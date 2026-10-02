using System;

namespace Homespool.Host.Cameras;

/// <summary>
/// Whether a browser's WebRTC offer only asks to receive, which is the one kind Homespool forwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stream server takes its role from the offer.</b> go2rtc reads each media line's direction:
/// an offer that sends video joins the camera's stream as another source of it, and is served to
/// every viewer in the camera's place whenever the camera cannot be reached. An offer that receives
/// video but sends audio joins as a viewer whose microphone reaches the camera's speaker, which go2rtc
/// turns on for every <c>rtsp://</c> source. Both are a viewer doing more than viewing, so the offer
/// is read for its directions before it is forwarded - and for nothing else, since the codecs and
/// addresses in it are a negotiation between the browser and the sidecar.
/// </para>
/// <para>
/// <b>Every media line must say <c>recvonly</c> itself.</b> A line with no direction is
/// <c>sendrecv</c> by the standard, and a line's own direction overrides the session's - Firefox
/// sends a session-level <c>sendrecv</c> and relies on exactly that - so the session level is not
/// read. Only video and audio lines are accepted, and at least one must be video: the page's own
/// offer is a single receive-only video line.
/// </para>
/// <para>
/// <b>Lines are split as the sidecar's parser splits them</b>: on <c>\n</c>, with one <c>\r</c>
/// before it dropped. A <c>\r</c> anywhere else refuses the offer, because that parser shortens a
/// line by one character for each <c>\r</c> in it, and an offer read two ways is the opening this
/// check closes. For the same reason a final line must end in <c>\n</c>, and a direction is
/// recognised ignoring case and surrounding space but accepted only as exactly <c>recvonly</c>.
/// </para>
/// </remarks>
public static class WebRtcOfferDirection
{
    private const string Receive = "recvonly";

    private static readonly string[] Directions = ["sendrecv", "sendonly", Receive, "inactive"];

    /// <summary>
    /// Whether every media line in <paramref name="sdp"/> receives and none sends.
    /// </summary>
    /// <param name="sdp">The offer's session description, as the browser wrote it.</param>
    /// <returns>
    /// <see langword="true"/> for an offer of video and perhaps audio, every line of it explicitly
    /// <c>recvonly</c>; <see langword="false"/> for anything else, including an offer this cannot
    /// read the same way the sidecar would.
    /// </returns>
    public static bool OnlyReceives(string sdp)
    {
        ArgumentNullException.ThrowIfNull(sdp);

        if (!sdp.EndsWith('\n'))
        {
            return false;
        }

        bool inMedia = false;
        bool mediaReceives = false;
        bool videoReceives = false;
        bool isVideo = false;

        foreach (string raw in sdp[..^1].Split('\n'))
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;

            if (line.Contains('\r', StringComparison.Ordinal))
            {
                return false;
            }

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (inMedia && !mediaReceives)
                {
                    return false;
                }

                string kind = line[2..].Split(' ', '\t')[0];

                if (kind is not ("video" or "audio"))
                {
                    return false;
                }

                inMedia = true;
                mediaReceives = false;
                isVideo = kind == "video";
            }
            else if (inMedia && line.StartsWith("a=", StringComparison.Ordinal))
            {
                string attribute = line[2..];
                int colon = attribute.IndexOf(':', StringComparison.Ordinal);
                string key = colon > 0 ? attribute[..colon] : attribute;

                if (!IsDirection(key))
                {
                    continue;
                }

                if (key != Receive)
                {
                    return false;
                }

                mediaReceives = true;
                videoReceives |= isVideo;
            }
        }

        return inMedia && mediaReceives && videoReceives;
    }

    private static bool IsDirection(string key)
    {
        string trimmed = key.Trim();

        return Array.Exists(Directions, direction => direction.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
