using System.Net;

using AwesomeAssertions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// The two halves of what a camera address may be: its shape, checked when somebody types it, and
/// the address it resolves to, checked when the connection is made.
/// </summary>
public class CameraAddressPolicyTests
{
    [Theory]
    [InlineData("http://camera:1984/api/frame.jpeg?src=coreone")]
    [InlineData("https://cam.example/snapshot")]
    [InlineData("http://192.168.13.217/still.jpg")]
    public void AnHttpAddressIsAccepted(string address)
    {
        CameraAddressCheck check = CameraAddressPolicy.Inspect(address);

        check.IsAcceptable.Should().BeTrue();
        check.Uri.Should().NotBeNull();
        check.Error.Should().BeNull();
    }

    /// <summary>
    /// An RTSP address is the one refusal a person is likely to hit, because it is a reasonable
    /// thing to try - so the message has to say what to do instead. "Invalid URL" would send
    /// someone hunting for a typo that is not there.
    /// </summary>
    [Fact]
    public void AnRtspAddressIsRefusedWithAnAnswerRatherThanAComplaint()
    {
        CameraAddressCheck check = CameraAddressPolicy.Inspect("rtsp://192.168.13.217/live");

        check.IsAcceptable.Should().BeFalse();
        check.Error.Should().Contain("go2rtc", "the refusal has to point somewhere useful");
        check.Error.Should().Contain("snapshot");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("camera:1984/snapshot")]
    [InlineData("file:///etc/passwd")]
    public void AnythingElseIsRefused(string address)
    {
        CameraAddressCheck check = CameraAddressPolicy.Inspect(address);

        check.IsAcceptable.Should().BeFalse();
        check.Error.Should().NotBeNullOrWhiteSpace();
        check.Uri.Should().BeNull();
    }

    /// <summary>
    /// The addresses that make this a server-side request forgery surface rather than a fetch:
    /// the application's own ports, the container beside it, and cloud metadata.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.9.9.9")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    public void LoopbackAndLinkLocalAreNotReachable(string address)
    {
        CameraAddressPolicy.IsReachableAddress(IPAddress.Parse(address)).Should().BeFalse();
    }

    /// <summary>
    /// Everything else is allowed on purpose. A private-range allowlist would be right for a hosted
    /// service and wrong here, where the ordinary case is a camera on the same LAN.
    /// </summary>
    [Theory]
    [InlineData("192.168.13.217")]
    [InlineData("10.0.0.5")]
    [InlineData("172.28.0.3")]
    [InlineData("1.1.1.1")]
    [InlineData("2001:db8::1")]
    public void OrdinaryAddressesAreReachable(string address)
    {
        CameraAddressPolicy.IsReachableAddress(IPAddress.Parse(address)).Should().BeTrue();
    }
}
