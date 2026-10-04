using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="WebRtcSidecarWriter.Matches"/> - whether the sidecar's configuration already says what a
/// write would, so the restart that drops every viewer can be skipped.
/// </summary>
public sealed class WebRtcSidecarMatchTests
{
    private const string Candidate = "192.0.2.10:8555";
    private const string StunServer = "stun:stun.l.google.com:19302";

    private const string StunOff =
        "webrtc:\n" +
        "  candidates:\n" +
        "    - 192.0.2.10:8555\n" +
        "  ice_servers: []\n";

    private const string StunOn =
        "webrtc:\n" +
        "  candidates:\n" +
        "    - 192.0.2.10:8555\n" +
        "  ice_servers:\n" +
        "    - urls:\n" +
        "        - stun:stun.l.google.com:19302\n";

    /// <summary>The section as a write leaves it, with STUN either way, matches that choice and not the other.</summary>
    [Fact]
    public void TheSectionAsWrittenMatchesItsOwnStunChoice()
    {
        WebRtcSidecarWriter.Matches(StunOff, Candidate, stunEnabled: false, StunServer).Should().BeTrue();
        WebRtcSidecarWriter.Matches(StunOn, Candidate, stunEnabled: true, StunServer).Should().BeTrue();

        WebRtcSidecarWriter.Matches(StunOff, Candidate, stunEnabled: true, StunServer).Should().BeFalse();
        WebRtcSidecarWriter.Matches(StunOn, Candidate, stunEnabled: false, StunServer).Should().BeFalse();
    }

    /// <summary>The document a write sends is JSON, and reads the same as go2rtc's YAML rendering of it.</summary>
    [Fact]
    public void TheJsonAWriteSendsMatchesToo()
    {
        WebRtcSidecarWriter.Matches(
                "{\"webrtc\":{\"candidates\":[\"192.0.2.10:8555\"],\"ice_servers\":[]}}",
                Candidate,
                stunEnabled: false,
                StunServer)
            .Should().BeTrue();
    }

    /// <summary>
    /// The streams share the document, and a source is whatever somebody typed. One carrying the
    /// candidate does not stand in for a <c>webrtc</c> section that lacks it.
    /// </summary>
    [Fact]
    public void ACandidateInACameraSourceIsNotAdvertised()
    {
        string document =
            "webrtc:\n" +
            "  candidates:\n" +
            "    - 192.0.2.99:8555\n" +
            "  ice_servers: []\n" +
            "streams:\n" +
            "  garage:\n" +
            "    - rtsp://192.0.2.30/live?next=192.0.2.10:8555\n";

        WebRtcSidecarWriter.Matches(document, Candidate, stunEnabled: false, StunServer).Should().BeFalse();
    }

    /// <summary>Nor does a source carrying the STUN address switch STUN on.</summary>
    [Fact]
    public void AStunAddressInACameraSourceIsNotAStunServer()
    {
        string document = StunOff +
                          "streams:\n" +
                          "  garage:\n" +
                          "    - rtsp://192.0.2.30/live?x=stun:stun.l.google.com:19302\n";

        WebRtcSidecarWriter.Matches(document, Candidate, stunEnabled: true, StunServer).Should().BeFalse();
        WebRtcSidecarWriter.Matches(document, Candidate, stunEnabled: false, StunServer).Should().BeTrue();
    }

    /// <summary>
    /// A missing <c>ice_servers</c> is go2rtc's public STUN default, not STUN off - so it is written,
    /// even though no STUN address appears anywhere in the document.
    /// </summary>
    [Fact]
    public void NoIceServersIsNotStunOff()
    {
        WebRtcSidecarWriter.Matches(
                "webrtc:\n  candidates:\n    - 192.0.2.10:8555\n",
                Candidate,
                stunEnabled: false,
                StunServer)
            .Should().BeFalse();
    }

    /// <summary>A write replaces both lists whole, so anything beyond what it would write is not a match.</summary>
    [Theory]
    [InlineData("webrtc:\n  candidates:\n    - 192.0.2.10:8555\n    - 192.0.2.11:8555\n  ice_servers: []\n")]
    [InlineData("webrtc:\n  candidates:\n    - 192.0.2.10:85550\n  ice_servers: []\n")]
    [InlineData("webrtc:\n  candidates: 192.0.2.10:8555\n  ice_servers: []\n")]
    [InlineData("webrtc:\n  candidates:\n    - 192.0.2.10:8555\n  ice_servers:\n    - urls: []\n")]
    public void AnythingElseInTheSectionIsNotAMatch(string document)
    {
        WebRtcSidecarWriter.Matches(document, Candidate, stunEnabled: false, StunServer).Should().BeFalse();
    }

    /// <summary>A second STUN server, or a second address for the one, is not the single one configured.</summary>
    [Theory]
    [InlineData("    - urls:\n        - stun:stun.l.google.com:19302\n        - stun:192.0.2.40:3478\n")]
    [InlineData("    - urls:\n        - stun:stun.l.google.com:19302\n    - urls:\n        - stun:192.0.2.40:3478\n")]
    [InlineData("    - urls:\n        - stun:stun.l.google.com:19302\n      username: someone\n")]
    public void AnythingBeyondTheOneStunServerIsNotAMatch(string servers)
    {
        string document = "webrtc:\n  candidates:\n    - 192.0.2.10:8555\n  ice_servers:\n" + servers;

        WebRtcSidecarWriter.Matches(document, Candidate, stunEnabled: true, StunServer).Should().BeFalse();
    }

    /// <summary>A document with no <c>webrtc</c> section, or one that is not YAML, says nothing and is written.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("streams:\n  garage:\n    - rtsp://192.0.2.10:8555/live\n")]
    [InlineData("webrtc: 192.0.2.10:8555\n")]
    [InlineData("webrtc: [\n")]
    [InlineData("webrtc:\n  candidates:\n    - 192.0.2.10:8555\n  candidates:\n    - 192.0.2.10:8555\n  ice_servers: []\n")]
    public void ADocumentWithoutAReadableSectionIsNotAMatch(string document)
    {
        WebRtcSidecarWriter.Matches(document, Candidate, stunEnabled: false, StunServer).Should().BeFalse();
    }
}
