using System;
using System.IO;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// Which WebRTC offers Homespool forwards to the sidecar: those that only receive.
/// </summary>
/// <remarks>
/// <para>
/// <b>The first group is real browsers.</b> Each offer was made by the page's own call in Chromium,
/// Firefox and WebKit, so a rule that refuses one of them refuses that browser's viewers. Firefox's
/// is the one that matters: it carries a session-level <c>sendrecv</c> and a <c>recvonly</c> on its
/// video line, so a check that read the session level would refuse it.
/// </para>
/// <para>
/// <b>The rest are those offers turned into what the sidecar would treat as more than a viewer</b>:
/// a video line that sends, which makes the connection a source of the camera's stream, and an audio
/// line that sends, which reaches the camera's speaker. Then the shapes that would let the sidecar's
/// parser read an offer differently from this one.
/// </para>
/// </remarks>
public sealed class WebRtcOfferDirectionTests
{
    private const string ReceiveVideo = "\r\na=recvonly\r\n";

    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public void ABrowsersOfferToWatchIsForwarded(string browser)
    {
        WebRtcOfferDirection.OnlyReceives(BrowserOffer(browser)).Should().BeTrue();
    }

    [Theory]
    [InlineData("chromium", "sendonly")]
    [InlineData("chromium", "sendrecv")]
    [InlineData("chromium", "inactive")]
    [InlineData("firefox", "sendonly")]
    [InlineData("firefox", "sendrecv")]
    [InlineData("firefox", "inactive")]
    [InlineData("webkit", "sendonly")]
    [InlineData("webkit", "sendrecv")]
    [InlineData("webkit", "inactive")]
    public void ABrowsersOfferWhoseVideoDoesNotOnlyReceiveIsRefused(string browser, string direction)
    {
        // sendonly and sendrecv are what make the sidecar add the connection as a producer; inactive
        // is refused only because the page never sends it.
        string offer = BrowserOffer(browser).Replace(ReceiveVideo, $"\r\na={direction}\r\n", StringComparison.Ordinal);

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public void ABrowsersOfferWhoseVideoStatesNoDirectionIsRefused(string browser)
    {
        // No direction is sendrecv by the standard, and the sidecar skips the line altogether - which
        // leaves it an offer with no video to receive, and so a producer.
        string offer = BrowserOffer(browser).Replace(ReceiveVideo, "\r\n", StringComparison.Ordinal);

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public void ABrowsersOfferMayAlsoReceiveAudio(string browser)
    {
        WebRtcOfferDirection.OnlyReceives(BrowserOffer(browser) + Audio("recvonly")).Should().BeTrue();
    }

    [Theory]
    [InlineData("chromium", "sendrecv")]
    [InlineData("chromium", "sendonly")]
    [InlineData("firefox", "sendrecv")]
    [InlineData("firefox", "sendonly")]
    [InlineData("webkit", "sendrecv")]
    [InlineData("webkit", "sendonly")]
    public void ABrowsersOfferThatSendsAudioIsRefused(string browser, string direction)
    {
        // The speaker: video still only receives, so the sidecar adds this as a viewer - and pairs
        // its audio with any source that takes audio, which an rtsp:// camera with one does.
        WebRtcOfferDirection.OnlyReceives(BrowserOffer(browser) + Audio(direction)).Should().BeFalse();
    }

    [Fact]
    public void AnOfferWithNoMediaIsRefused()
    {
        WebRtcOfferDirection.OnlyReceives("v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n").Should().BeFalse();
    }

    [Fact]
    public void AnOfferOfAudioAloneIsRefused()
    {
        WebRtcOfferDirection.OnlyReceives(Session() + Audio("recvonly")).Should().BeFalse();
    }

    [Fact]
    public void AnOfferWithADataChannelIsRefusedEvenOneThatSaysRecvonly()
    {
        // The page opens no data channel, so only video and audio lines are forwarded at all.
        string offer = Session() + Video("recvonly") +
                       "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\na=mid:1\r\na=recvonly\r\n";

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Fact]
    public void ALastLineWithNoDirectionIsRefusedWhenAnEarlierOneReceives()
    {
        WebRtcOfferDirection.OnlyReceives(Session() + Video("recvonly") + Video(direction: null)).Should().BeFalse();
    }

    [Fact]
    public void AnEarlierLineWithNoDirectionIsRefusedWhenALaterOneReceives()
    {
        // Each line is judged when the next begins, so the last one receiving cannot vouch for it.
        string offer = Session() + Video(direction: null) + Video("recvonly");

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Fact]
    public void ASecondVideoLineThatSendsIsRefused()
    {
        WebRtcOfferDirection.OnlyReceives(Session() + Video("recvonly") + Video("sendonly")).Should().BeFalse();
    }

    [Theory]
    [InlineData("sendrecv")]
    [InlineData("sendonly")]
    public void ASessionDirectionIsOverriddenByTheLinesOwn(string direction)
    {
        // The standard's rule, and the sidecar reads each line's own as well.
        WebRtcOfferDirection.OnlyReceives(Session($"a={direction}\r\n") + Video("recvonly")).Should().BeTrue();
    }

    [Theory]
    [InlineData("a=SENDONLY")]
    [InlineData("a=Sendrecv")]
    [InlineData("a= sendonly")]
    [InlineData("a=sendonly ")]
    [InlineData("a=sendonly:x")]
    public void ADirectionWrittenLooselyBesideRecvonlyIsRefused(string line)
    {
        string offer = Session() + Video("recvonly") + line + "\r\n";

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Theory]
    [InlineData("a=RECVONLY")]
    [InlineData("a=recvonly ")]
    [InlineData("a= recvonly")]
    public void RecvonlyWrittenLooselyIsNotRecvonly(string line)
    {
        // The sidecar matches the word exactly, so it would see no direction on this line at all.
        string offer = Session() + Video(direction: null) + line + "\r\n";

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Fact]
    public void AnOfferWithBareNewlinesIsReadTheSame()
    {
        string offer = BrowserOffer("chromium").Replace("\r\n", "\n", StringComparison.Ordinal);

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeTrue();
    }

    [Theory]
    [InlineData("a=recvonly\r\r\n")]
    [InlineData("a=recv\ronly\r\n")]
    [InlineData("a=recvonly\ra=sendonly\r\n")]
    public void AnOfferWithACarriageReturnAnywhereElseIsRefused(string line)
    {
        // The sidecar's parser shortens a line by one character for every \r in it, so this offer
        // would not say the same thing to both readers.
        string offer = Session() + Video(direction: null) + line;

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Fact]
    public void AnAudioLineHiddenBehindCarriageReturnsIsRefused()
    {
        // The sidecar's parser skips a \r before a line's type, so it reads a sending audio line here
        // that a split on \n alone would fold, unseen, into the video line above it.
        string offer = BrowserOffer("chromium") +
                       "\rm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n\ra=sendrecv\r\n\ra=rtpmap:111 opus/48000/2\r\n";

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    [Fact]
    public void AnOfferWhoseLastLineIsUnfinishedIsRefused()
    {
        // The sidecar's parser fails on a line with no end, and this one would otherwise be read.
        string offer = Session() + Video("recvonly") + "a=rtpmap:97 VP8/90000";

        WebRtcOfferDirection.OnlyReceives(offer).Should().BeFalse();
    }

    private static string BrowserOffer(string browser)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"webrtc-offer-{browser}.sdp"));
    }

    private static string Session(string attributes = "")
    {
        return "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n" + attributes;
    }

    private static string Video(string? direction)
    {
        return "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=rtpmap:96 H264/90000\r\n" +
               (direction is null ? string.Empty : $"a={direction}\r\n");
    }

    private static string Audio(string direction)
    {
        return $"m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\na={direction}\r\na=rtpmap:111 opus/48000/2\r\n";
    }
}
