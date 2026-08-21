using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// What a camera's source shows, and what survives an edit that never saw the password.
/// </summary>
/// <remarks>
/// <para>
/// The sources here are real shapes rather than invented ones: <c>onvif://user:pass@…</c> is the
/// spelling go2rtc's own documentation advertises, and <c>ffmpeg:device?video=…</c> is what an
/// attached camera stores - a source with no authority at all, which the masking has to leave alone
/// rather than mangle.
/// </para>
/// <para>
/// The last group is the one that matters most. Hiding the password is easy; hiding it without
/// destroying it on the next save is the part that needs saying out loud, because the form posts the
/// mask straight back and a manager who only corrected a name would otherwise write <c>****</c> into
/// the database and break the camera.
/// </para>
/// </remarks>
public sealed class CameraSourceDisplayTests
{
    [Theory]
    [InlineData("rtsp://admin:hunter2@192.168.1.50/Streaming/Channels/101",
                "rtsp://192.168.1.50/Streaming/Channels/101")]
    [InlineData("onvif://user:pass@192.168.1.123:80", "onvif://192.168.1.123:80")]
    [InlineData("http://someone:secret@camera.local/snapshot.jpg", "http://camera.local/snapshot.jpg")]
    public void TheListDropsTheWholeCredential(string source, string expected)
    {
        // A user name is half a credential, and a viewer needs the address rather than the account.
        CameraSourceDisplay.WithoutCredential(source).Should().Be(expected);
    }

    [Theory]
    [InlineData("rtsp://admin:hunter2@192.168.1.50/live", "rtsp://admin:****@192.168.1.50/live")]
    [InlineData("onvif://user:pass@192.168.1.123:80", "onvif://user:****@192.168.1.123:80")]
    public void TheEditFormHidesThePasswordAndKeepsTheUserName(string source, string expected)
    {
        // Behind ManageCamera, and whoever is editing needs to know which account this camera uses.
        CameraSourceDisplay.WithHiddenPassword(source).Should().Be(expected);
    }

    [Theory]
    [InlineData("rtsp://192.168.1.50/live")]
    [InlineData("ffmpeg:device?video=/dev/v4l/by-id/usb-046d_0821-video-index0&input_format=mjpeg")]
    [InlineData("http://camera.local/snapshot.jpg")]
    public void ASourceCarryingNoCredentialIsUntouched(string source)
    {
        CameraSourceDisplay.WithoutCredential(source).Should().Be(source);
        CameraSourceDisplay.WithHiddenPassword(source).Should().Be(source);
    }

    [Fact]
    public void AUserNameWithNoPasswordIsLeftAlone()
    {
        // Nothing to hide, and therefore nothing to put back later.
        CameraSourceDisplay.WithHiddenPassword("rtsp://admin@192.168.1.50/live")
                           .Should()
                           .Be("rtsp://admin@192.168.1.50/live");
    }

    [Fact]
    public void AnEditThatNeverTouchedThePasswordKeepsTheStoredOne()
    {
        // The hazard this class exists for: the form posts back what it was shown.
        const string stored = "rtsp://admin:hunter2@192.168.1.50/live";
        string submitted = CameraSourceDisplay.WithHiddenPassword(stored);

        CameraSourceDisplay.RestoreHiddenPassword(submitted, stored).Should().Be(stored);
    }

    [Fact]
    public void AChangedAddressStillKeepsTheStoredPassword()
    {
        // Keyed on the placeholder rather than on the whole source being unchanged, so a host can be
        // corrected without re-typing a password nobody was shown.
        const string stored = "rtsp://admin:hunter2@192.168.1.50/live";

        CameraSourceDisplay.RestoreHiddenPassword("rtsp://admin:****@192.168.1.77/live", stored)
                           .Should()
                           .Be("rtsp://admin:hunter2@192.168.1.77/live");
    }

    [Fact]
    public void ATypedPasswordReplacesTheStoredOne()
    {
        // The one case that must keep working, or a password could never be changed.
        const string stored = "rtsp://admin:hunter2@192.168.1.50/live";

        CameraSourceDisplay.RestoreHiddenPassword("rtsp://admin:newsecret@192.168.1.50/live", stored)
                           .Should()
                           .Be("rtsp://admin:newsecret@192.168.1.50/live");
    }

    [Fact]
    public void AUserNameMayBeChangedWhileThePasswordRidesAlong()
    {
        const string stored = "rtsp://admin:hunter2@192.168.1.50/live";

        CameraSourceDisplay.RestoreHiddenPassword("rtsp://operator:****@192.168.1.50/live", stored)
                           .Should()
                           .Be("rtsp://operator:hunter2@192.168.1.50/live");
    }

    [Fact]
    public void ACredentialIsNotInventedForASourceThatNeverHadOne()
    {
        // Nothing stored to restore, so the placeholder is left exactly as submitted rather than
        // being quietly turned into a password.
        CameraSourceDisplay.RestoreHiddenPassword("rtsp://admin:****@192.168.1.50/live",
                                                  "rtsp://192.168.1.50/live")
                           .Should()
                           .Be("rtsp://admin:****@192.168.1.50/live");
    }
}
