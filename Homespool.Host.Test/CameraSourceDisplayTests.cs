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
    [InlineData("rtsp://admin:hunter2@192.168.1.50/Streaming/Channels/101", "rtsp://192.168.1.50")]
    [InlineData("onvif://user:pass@192.168.1.123:80", "onvif://192.168.1.123:80")]
    [InlineData("http://someone:secret@camera.local/snapshot.jpg", "http://camera.local")] // betterleaks:allow - a test fixture for a camera that does not exist
    [InlineData("rtsp://admin@192.168.1.50/live", "rtsp://192.168.1.50")]
    [InlineData("rtsp://admin:p@ss@192.168.1.50/live", "rtsp://192.168.1.50")]
    public void TheListDropsTheWholeCredential(string source, string expected)
    {
        // A user name is half a credential, and a viewer needs the address rather than the account.
        CameraSourceDisplay.AddressOnly(source).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://192.168.1.60:88/cgi-bin/CGIProxy.fcgi?cmd=snapPicture2&usr=admin&pwd=hunter2",
                "http://192.168.1.60:88")]
    [InlineData("http://192.168.1.61/flv?port=1935&app=bcs&stream=channel0_main.bcs&user=admin&password=hunter2",
                "http://192.168.1.61")]
    [InlineData("http://192.168.1.62:81/videostream.cgi?loginuse=admin&loginpas=hunter2", "http://192.168.1.62:81")]
    [InlineData("rtsp://192.168.1.63:554/user=admin&password=hunter2&channel=1&stream=0.sdp", "rtsp://192.168.1.63:554")]
    [InlineData("rtsp://192.168.1.64/live#transport=tcp://admin:hunter2@192.168.1.65", "rtsp://192.168.1.64")]
    [InlineData("rtsp://192.168.1.66?password=hunter2", "rtsp://192.168.1.66")]
    public void TheListDropsACredentialCarriedAnywhereElse(string source, string expected)
    {
        // Foscam, Reolink, the Foscam clones and Xiongmai, then go2rtc's own options after '#': the
        // credential is wherever the camera's firmware decided, so the list shows none of it.
        CameraSourceDisplay.AddressOnly(source).Should().Be(expected);
    }

    [Theory]
    [InlineData("rtsp://admin:hunter2@[fe80::1]:554/live", "rtsp://[fe80::1]:554")]
    [InlineData("rtsps://Camera.Local:322/Stream", "rtsps://Camera.Local:322")]
    public void TheListKeepsTheHostAndPortAsTyped(string source, string expected)
    {
        // Which device this is is the one thing a viewer needs, and it is not normalised on the way.
        CameraSourceDisplay.AddressOnly(source).Should().Be(expected);
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
    public void TheEditFormLeavesASourceCarryingNoCredentialUntouched(string source)
    {
        CameraSourceDisplay.WithHiddenPassword(source).Should().Be(source);
    }

    [Fact]
    public void TheListShowsAnAttachedCameraAsItIs()
    {
        // Not a URL, and nothing in it is a credential - it names a device on this server.
        const string source = "ffmpeg:device?video=/dev/v4l/by-id/usb-046d_0821-video-index0&input_format=mjpeg";

        CameraSourceDisplay.AddressOnly(source).Should().Be(source);
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

    [Theory]
    [InlineData("rtsp://admin:****@192.168.1.77/live")]
    [InlineData("rtsp://admin:****@attacker.example/live")]
    [InlineData("rtsp://admin:****@192.168.1.50:8554/live")]
    [InlineData("http://admin:****@192.168.1.50/live")]
    [InlineData("ffmpeg:rtsp://admin:****@192.168.1.50/live")]
    [InlineData("rtsp://operator:****@192.168.1.50/live")]
    [InlineData("rtsp://Admin:****@192.168.1.50/live")]
    [InlineData("rtsp://admin:****@192.168.1.50/other/stream")]
    [InlineData("rtsp://admin:****@192.168.1.50/live?channel=2")]
    [InlineData("rtsp://admin:****@192.168.1.50/live#transport=ws://attacker.example/x")]
    [InlineData("RTSP://admin:****@192.168.1.50/live")]
    [InlineData("rtsp://admin:****@192.168.1.50./live")]
    public void ThePasswordIsNotCarriedIntoAnAlteredSource(string submitted)
    {
        // Anything but the password differing means the address may have moved, and what the sidecar
        // treats as an address is decided in its source tree, not this one: go2rtc reads what follows
        // '#' as options, and transport= is an address it dials instead of the host in front of it.
        const string stored = "rtsp://admin:hunter2@192.168.1.50/live";

        string restored = CameraSourceDisplay.RestoreHiddenPassword(submitted, stored);

        restored.Should().Be(submitted);
        restored.Should().NotContain("hunter2");
        CameraSourceDisplay.CarriesHiddenPassword(restored).Should().BeTrue();
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
    public void ATypedPasswordMayGoToANewServer()
    {
        // Somebody who types the password knows it, so there is nothing to protect by refusing.
        const string stored = "rtsp://admin:hunter2@192.168.1.50/live";

        CameraSourceDisplay.RestoreHiddenPassword("rtsp://admin:newsecret@192.168.1.77/live", stored)
                           .Should()
                           .Be("rtsp://admin:newsecret@192.168.1.77/live");
    }

    [Theory]
    [InlineData("rtsp://admin:****@192.168.1.50/live", true)]
    [InlineData("rtsp://admin:hunter2@192.168.1.50/live", false)]
    [InlineData("rtsp://admin@192.168.1.50/live", false)]
    [InlineData("rtsp://192.168.1.50/live", false)]
    [InlineData("rtsp://192.168.1.50/****", false)]
    [InlineData("ffmpeg:device?video=/dev/video0", false)]
    public void OnlyAPasswordThatIsThePlaceholderCounts(string source, bool expected)
    {
        CameraSourceDisplay.CarriesHiddenPassword(source).Should().Be(expected);
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

    [Theory]
    [InlineData("rtsp://admin:hunter2@192.168.1.50/live", "rtsp://192.168.1.50/live", "admin", "hunter2")]
    [InlineData("onvif://user:pass@192.168.1.123:80", "onvif://192.168.1.123:80", "user", "pass")]
    [InlineData("rtsp://admin@192.168.1.50/live", "rtsp://192.168.1.50/live", "admin", null)]
    [InlineData("rtsp://192.168.1.50/live", "rtsp://192.168.1.50/live", null, null)]
    [InlineData("ffmpeg:device?video=/dev/video0", "ffmpeg:device?video=/dev/video0", null, null)]
    public void ASourceComesApartIntoAnAddressAndACredential(string source,
                                                             string address,
                                                             string? user,
                                                             string? password)
    {
        CameraSourceParts parts = CameraSourceDisplay.SplitCredential(source);

        parts.Address.Should().Be(address);
        parts.User.Should().Be(user);
        parts.Password.Should().Be(password);
    }

    [Theory]
    [InlineData("rtsp://192.168.1.50/live", "admin", "hunter2", "rtsp://admin:hunter2@192.168.1.50/live")]
    [InlineData("rtsp://192.168.1.50/live", "admin", null, "rtsp://admin@192.168.1.50/live")]
    [InlineData("rtsp://192.168.1.50/live", null, null, "rtsp://192.168.1.50/live")]
    [InlineData("ffmpeg:device?video=/dev/video0", "admin", "x", "ffmpeg:device?video=/dev/video0")]
    public void AnAddressAndACredentialGoBackTogether(string address,
                                                      string? user,
                                                      string? password,
                                                      string expected)
    {
        CameraSourceDisplay.WithCredential(address, user, password).Should().Be(expected);
    }

    [Theory]
    [InlineData("rtsp://admin:hunter2@192.168.1.50/live")]
    [InlineData("onvif://user:pass@192.168.1.123:80")]
    [InlineData("rtsp://admin@192.168.1.50/live")]
    [InlineData("rtsp://192.168.1.50/live")]
    [InlineData("ffmpeg:device?video=/dev/video0&input_format=mjpeg")]
    public void TakingASourceApartAndPuttingItBackChangesNothing(string source)
    {
        // The round trip is what the stream server depends on: the sidecar has to receive the source
        // byte for byte, or it connects to a subtly different address than the one that was checked.
        CameraSourceParts parts = CameraSourceDisplay.SplitCredential(source);

        CameraSourceDisplay.WithCredential(parts.Address, parts.User, parts.Password)
                           .Should()
                           .Be(source);
    }
}
