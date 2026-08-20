using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// Choosing a camera's capture size: what the stream server's device listing means, and what a
/// source string says when nobody expressed a preference.
/// </summary>
/// <remarks>
/// The listings below are **captured from the real endpoint** on the appliance (2026-08-20), not
/// invented - including the two things that make a naive parser wrong: the sizes arrive in the
/// camera's own order rather than sorted, and one camera repeats a size.
/// </remarks>
public class CameraResolutionTests
{
    /// <summary>A Logitech C910 as <c>/api/ffmpeg/devices</c> reported it.</summary>
    private const string C910Sizes =
        "640x480 160x120 176x144 320x176 320x240 432x240 352x288 544x288 640x360 752x416 800x448 "
        + "864x480 960x544 1024x576 800x600 1184x656 960x720 1280x720 1392x768 1504x832 1600x896 "
        + "1280x960 1712x960 1792x1008 1920x1080 1600x1200 2048x1536 2592x1944";

    /// <summary>A no-name PC-W3, which repeats 1280x720 and lists it first.</summary>
    private const string PcW3Sizes = "1280x720 320x240 640x360 640x480 800x600 1024x768 1280x960 1920x1080 1280x720";

    private static Go2RtcClient.DeviceSource Mjpeg(string node, string sizes)
    {
        return new Go2RtcClient.DeviceSource(
            "Motion-JPEG",
            sizes,
            $"ffmpeg:device?video=/dev/{node}&input_format=mjpeg&video_size=640x480");
    }

    private static Go2RtcClient.DeviceSource Raw(string node, string sizes)
    {
        return new Go2RtcClient.DeviceSource(
            "YUYV 4:2:2",
            sizes,
            $"ffmpeg:device?video=/dev/{node}&input_format=yuyv422&video_size=640x480#video=h264#hardware");
    }

    [Fact]
    public void SizesAreOrderedSmallestFirstWhateverOrderTheCameraGaveThem()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> byNode = Go2RtcClient.ParseDeviceFormats([Mjpeg("video0", C910Sizes)]);

        byNode["video0"][0].Should().Be("160x120");
        byNode["video0"][^1].Should().Be("2592x1944");
    }

    /// <summary>
    /// The PC-W3 lists 1280x720 twice - once at the front and once at the end - so a select built
    /// from this without de-duplicating shows the same entry twice.
    /// </summary>
    [Fact]
    public void ARepeatedSizeIsListedOnce()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> byNode = Go2RtcClient.ParseDeviceFormats([Mjpeg("video2", PcW3Sizes)]);

        byNode["video2"].Count(size => size == "1280x720").Should().Be(1);
    }

    /// <summary>
    /// Every device reports its raw format beside its compressed one, and taking that would undo the
    /// reason the source states <c>input_format=mjpeg</c> - a transcode per frame, invisibly.
    /// </summary>
    [Fact]
    public void OnlyTheMjpegEntryIsRead()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> byNode = Go2RtcClient.ParseDeviceFormats(
        [
            Raw("video0", "1920x1080"),
            Mjpeg("video0", "640x360 1280x720"),
        ]);

        byNode["video0"].Should().Equal(["640x360", "1280x720"]);
    }

    [Fact]
    public void EachDeviceKeepsItsOwnSizes()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> byNode = Go2RtcClient.ParseDeviceFormats(
        [
            Mjpeg("video0", C910Sizes),
            Mjpeg("video2", PcW3Sizes),
        ]);

        byNode.Should().ContainKeys("video0", "video2");
        byNode["video0"].Should().Contain("2592x1944");
        byNode["video2"].Should().NotContain("2592x1944");
    }

    [Fact]
    public void RubbishInTheInfoFieldIsIgnoredRatherThanOffered()
    {
        // The field is free text; anything that is not WIDTHxHEIGHT must not reach a select.
        IReadOnlyDictionary<string, IReadOnlyList<string>> byNode = Go2RtcClient.ParseDeviceFormats([Mjpeg("video0", "640x360 Motion-JPEG : 1280x720 0x0")]);

        byNode["video0"].Should().Equal(["640x360", "1280x720"]);
    }

    [Fact]
    public void ADeviceWithNothingUsableIsAbsentRatherThanEmpty()
    {
        Go2RtcClient.ParseDeviceFormats([Mjpeg("video0", "nonsense")]).Should().BeEmpty();
    }

    /// <summary>
    /// Null resolution is the default and a real answer: it states no size, which is what leaves the
    /// camera and ffmpeg to settle it.
    /// </summary>
    [Fact]
    public void NoResolutionMeansNoVideoSizeAtAll()
    {
        string source = LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0");

        source.Should().NotContain("video_size");
        source.Should().Contain("input_format=mjpeg", "the format is stated rather than inherited whatever the size");
    }

    [Fact]
    public void AChosenResolutionIsStatedOnTheSource()
    {
        string source = LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0", "640x360");

        source.Should().EndWith("&video_size=640x360");
    }

    /// <summary>
    /// The edit form lets somebody change the source text and the size in one submission, so one of
    /// them has to win outright - and it is the resolution, or the row and the string it produced
    /// could disagree.
    /// </summary>
    [Fact]
    public void EditingReplacesWhateverSizeTheSubmittedSourceCarried()
    {
        string typed = LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0", "1920x1080");

        LocalCameraDevices.WithResolution(typed, "640x360")
                          .Should().EndWith("&video_size=640x360")
                          .And.Subject.Should().NotContain("1920x1080");
    }

    [Fact]
    public void ClearingTheChoiceRemovesTheSizeFromTheSource()
    {
        string typed = LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0", "1280x720");

        LocalCameraDevices.WithResolution(typed, null).Should().NotContain("video_size");
    }

    /// <summary>
    /// A network camera's size lives on the camera. Rewriting its source here would be inventing a
    /// setting this application cannot apply.
    /// </summary>
    [Fact]
    public void ANetworkSourceIsLeftExactlyAsItWas()
    {
        const string Rtsp = "rtsp://192.168.13.217/live";

        LocalCameraDevices.WithResolution(Rtsp, "640x360").Should().Be(Rtsp);
    }

    [Fact]
    public void TheDeviceNameIsReadBackOutOfASource()
    {
        LocalCameraDevices.DeviceNameFrom(LocalCameraDevices.SourceFor("usb-PC-W3_PC-W3-video-index0", "640x480"))
                          .Should().Be("usb-PC-W3_PC-W3-video-index0");

        LocalCameraDevices.DeviceNameFrom("rtsp://192.168.13.217/live").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyChoiceIsTheSameAsNoChoice(string? resolution)
    {
        LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0", resolution)
                          .Should().NotContain("video_size");
    }
}
