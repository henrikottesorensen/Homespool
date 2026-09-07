using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// What an attached camera's source may be, and why the answer is "the string we composed" rather
/// than "a string without anything nasty in it".
/// </summary>
/// <remarks>
/// <para>
/// <b>The stream server reads an <c>ffmpeg:</c> source as a command line.</b> It splits on <c>#</c>
/// and turns each fragment into an ffmpeg argument, so anything that reaches the composed string
/// through the device name or the capture size is an argument to a process running in a container
/// that can open every video device on the machine. Composing the source in our own code is not by
/// itself the protection: both halves it composes from arrive on a form.
/// </para>
/// <para>
/// The literal sources below are written out rather than produced by
/// <see cref="LocalCameraDevices.SourceFor"/>, so that a change to the composition has to be made
/// here too instead of silently agreeing with itself.
/// </para>
/// </remarks>
public class AttachedCameraSourceTests
{
    private const string Attached = "usb-046d_0821_437242E0-video-index0";

    private static readonly string[] Devices = [Attached, "usb-Generic_HD_Camera-video-index0"];

    private static CameraSourceCheck Check(string source, string? resolution = null)
    {
        return LocalCameraDevices.CheckComposed(source, resolution, Devices);
    }

    [Fact]
    public void TheSourceThePickerComposesIsAccepted()
    {
        Check($"ffmpeg:device?video=/dev/v4l/by-id/{Attached}&input_format=mjpeg")
            .IsAcceptable.Should().BeTrue();
    }

    [Fact]
    public void ASizeIsAcceptedWhenItIsTheOneComposedIntoTheSource()
    {
        Check($"ffmpeg:device?video=/dev/v4l/by-id/{Attached}&input_format=mjpeg&video_size=1280x720", "1280x720")
            .IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// The device name is one half of the composition, and a <c>#</c> in it is an ffmpeg argument.
    /// </summary>
    [Fact]
    public void ADeviceNameCarryingAnFfmpegArgumentIsRefused()
    {
        Check($"ffmpeg:device?video=/dev/v4l/by-id/{Attached}#raw=-i#raw=hosts&input_format=mjpeg")
            .IsAcceptable.Should().BeFalse();
    }

    /// <summary>
    /// The capture size is the other half, and it reaches the string unquoted.
    /// </summary>
    [Fact]
    public void ASizeThatIsNotTwoNumbersIsRefused()
    {
        Check($"ffmpeg:device?video=/dev/v4l/by-id/{Attached}&input_format=mjpeg&video_size=640x480#raw=-i",
              "640x480#raw=-i")
            .IsAcceptable.Should().BeFalse();
    }

    /// <summary>
    /// A source naming no device at all: the shape that passes a check written to look at the
    /// device name, because there is no device name in it to look at.
    /// </summary>
    [Fact]
    public void ADeviceSourceNamingNoDeviceIsRefused()
    {
        Check("ffmpeg:device?audio=x#raw=-i#raw=hosts")
            .IsAcceptable.Should().BeFalse();
    }

    [Fact]
    public void ADeviceThisMachineDoesNotHaveIsRefused()
    {
        Check("ffmpeg:device?video=/dev/v4l/by-id/usb-somebody_elses_camera-video-index0&input_format=mjpeg")
            .IsAcceptable.Should().BeFalse();
    }

    /// <summary>
    /// Everything the composition would have written, plus something it would not.
    /// </summary>
    [Fact]
    public void AnOtherwiseValidSourceWithSomethingAppendedIsRefused()
    {
        Check($"ffmpeg:device?video=/dev/v4l/by-id/{Attached}&input_format=mjpeg&video_size=640x480", null)
            .IsAcceptable.Should().BeFalse();
    }

    /// <summary>
    /// A refusal says which of the two problems it is, because they need different things done.
    /// </summary>
    [Fact]
    public void AnUnknownDeviceAndAnUncomposedSourceAreDifferentAnswers()
    {
        Check("ffmpeg:device?video=/dev/v4l/by-id/nothing-here&input_format=mjpeg")
            .Error!.Key.Should().Be("Cameras_AttachedDeviceUnknown");

        Check($"ffmpeg:device?video=/dev/v4l/by-id/{Attached}&input_format=mjpeg&fps=30")
            .Error!.Key.Should().Be("Cameras_AttachedSourceNotComposed");
    }

    /// <summary>
    /// Whitespace around a size is tidied rather than refused, because the composition tidies it
    /// too - and a size that only looks like one after trimming is still checked.
    /// </summary>
    [Fact]
    public void ASizeIsTrimmedBeforeItIsJudged()
    {
        Check($"ffmpeg:device?video=/dev/v4l/by-id/{Attached}&input_format=mjpeg&video_size=800x600", " 800x600 ")
            .IsAcceptable.Should().BeTrue();
    }
}
