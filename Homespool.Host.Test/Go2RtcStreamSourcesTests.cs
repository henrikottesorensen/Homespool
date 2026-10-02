using System.Collections.Generic;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="Go2RtcClient.ParseStreamSources"/> - which source each stream in go2rtc.yaml holds.
/// </summary>
public sealed class Go2RtcStreamSourcesTests
{
    /// <summary>
    /// go2rtc's own file, as the real image wrote it after a <c>PUT</c> of each source (Homespool's
    /// image, go2rtc 1.9.14): plain where it can, single-quoted for the one ending in a colon.
    /// </summary>
    private const string WrittenByGo2Rtc =
        "streams:\n" +
        "  00000000-0000-0000-0000-000000000001:\n" +
        "    - rtsp://192.0.2.10/live\n" +
        "  00000000-0000-0000-0000-000000000002:\n" +
        "    - ffmpeg:device?video=/dev/v4l/by-id/usb-046d_HD_Pro_Webcam_C920_ABCDEF12-video-index0&input_format=mjpeg&video_size=1920x1080\n" +
        "  00000000-0000-0000-0000-000000000003:\n" +
        "    - rtsp://admin:p#ss'w\"o:rd@192.0.2.11:554/Streaming/Channels/101?transportmode=unicast&profile=Profile_1\n" +
        "  00000000-0000-0000-0000-000000000007:\n" +
        "    - rtsp://192.0.2.14/kamera-æøå\n" +
        "  00000000-0000-0000-0000-000000000008:\n" +
        "    - http://192.0.2.15/x#frag\n" +
        "  00000000-0000-0000-0000-000000000022:\n" +
        "    - 'rtsp://192.0.2.20/a:b:'\n" +
        "  00000000-0000-0000-0000-000000000023:\n" +
        "    - rtsp://192.0.2.21/?a=[1,2]&b={c:d}&e=*f&g=!h&i=%25j\n" +
        "  00000000-0000-0000-0000-000000000024:\n" +
        "    - rtsp://192.0.2.22/#\n";

    [Fact]
    public void GoTwoRtcsOwnFileReadsBackAsEachSourceWasGiven()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sources = Go2RtcClient.ParseStreamSources(WrittenByGo2Rtc);

        sources.Should().NotBeNull();
        sources!["00000000-0000-0000-0000-000000000001"].Should().Equal("rtsp://192.0.2.10/live");
        sources["00000000-0000-0000-0000-000000000003"].Should().Equal(
            "rtsp://admin:p#ss'w\"o:rd@192.0.2.11:554/Streaming/Channels/101?transportmode=unicast&profile=Profile_1");
        sources["00000000-0000-0000-0000-000000000007"].Should().Equal("rtsp://192.0.2.14/kamera-æøå");
        sources["00000000-0000-0000-0000-000000000008"].Should().Equal("http://192.0.2.15/x#frag");
        sources["00000000-0000-0000-0000-000000000022"].Should().Equal("rtsp://192.0.2.20/a:b:");
        sources["00000000-0000-0000-0000-000000000023"].Should().Equal("rtsp://192.0.2.21/?a=[1,2]&b={c:d}&e=*f&g=!h&i=%25j");
        sources["00000000-0000-0000-0000-000000000024"].Should().Equal("rtsp://192.0.2.22/#");
        sources.Should().HaveCount(8);
    }

    /// <summary>An empty file - how a deployment starts - holds no streams, and is not a failure to read.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("streams: {}\n")]
    [InlineData("webrtc:\n  candidates:\n    - 192.0.2.1:8555\n")]
    public void AFileWithNoStreamsHoldsNone(string document)
    {
        Go2RtcClient.ParseStreamSources(document).Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>The other sections of the file are not streams, wherever they sit.</summary>
    [Fact]
    public void OtherSectionsAreNotStreams()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sources = Go2RtcClient.ParseStreamSources(
            "webrtc:\n  candidates:\n    - 192.0.2.1:8555\nstreams:\n  garage:\n    - rtsp://192.0.2.10/live\nrtsp:\n  listen: \":8554\"\n");

        sources.Should().BeEquivalentTo(new Dictionary<string, IReadOnlyList<string>>
        {
            ["garage"] = ["rtsp://192.0.2.10/live"],
        });
    }

    /// <summary>go2rtc also takes a stream's one source on its own, not in a list.</summary>
    [Fact]
    public void ASingleSourceWithoutAListIsOneSource()
    {
        Go2RtcClient.ParseStreamSources("streams:\n  garage: rtsp://192.0.2.10/live\n")!["garage"]
                    .Should().Equal("rtsp://192.0.2.10/live");
    }

    /// <summary>
    /// A stream whose value is any other shape holds no source a camera could have, so it is replaced
    /// rather than trusted.
    /// </summary>
    [Theory]
    [InlineData("streams:\n  garage:\n    url: rtsp://192.0.2.10/live\n")]
    [InlineData("streams:\n  garage:\n")]
    [InlineData("streams:\n  garage:\n    - [rtsp://192.0.2.10/live]\n")]
    public void AnyOtherShapeHoldsNoSource(string document)
    {
        Go2RtcClient.ParseStreamSources(document)!["garage"].Should().BeEmpty();
    }

    /// <summary>A file that is not YAML is not read as holding nothing.</summary>
    [Theory]
    [InlineData("streams: [\n")]
    [InlineData("streams:\n  garage:\n    - rtsp://a\n  garage:\n    - rtsp://b\n")]
    public void AFileThatIsNotYamlCannotBeRead(string document)
    {
        Go2RtcClient.ParseStreamSources(document).Should().BeNull();
    }
}
