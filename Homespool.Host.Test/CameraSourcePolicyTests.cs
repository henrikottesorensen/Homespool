using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Host.Cameras;
using Homespool.Host.Certificates;

namespace Homespool.Host.Test;

/// <summary>
/// What Homespool will and will not ask the stream server to reach on its behalf.
/// </summary>
/// <remarks>
/// The resolver is substituted, so "this name points at the server itself" is producible without a
/// DNS server - which is the whole reason <see cref="IHostAddressResolver"/> is an interface.
/// </remarks>
public class CameraSourcePolicyTests
{
    [Theory]
    [InlineData("rtsp://192.168.13.217/live")]
    [InlineData("rtsps://cam.example/live")]
    [InlineData("http://192.168.1.50/snapshot.jpg")]
    [InlineData("https://cam.example/snapshot")]
    [InlineData("rtmp://192.168.1.50/stream")]
    [InlineData("onvif://192.168.1.50")]
    [InlineData("onvif://user:pass@cam.example")]
    public async Task AnOrdinaryCameraAddressIsAccepted(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
        check.Error.Should().BeNull();
    }

    /// <summary>
    /// A local device names no host, so there is nothing to resolve and nothing to refuse. Whether
    /// the path exists is answered by trying it.
    /// </summary>
    [Fact]
    public async Task ALocalDeviceIsAccepted()
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(
            "ffmpeg:device?video=/dev/v4l/by-id/usb-046d_0821_437242E0-video-index0&input_format=mjpeg",
            CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// An allowlist rather than a denylist, so a source go2rtc grows later cannot arrive here by
    /// default. go2rtc's own API refuses exec: and echo: as well, but that is their guard, not ours.
    /// </summary>
    [Theory]
    [InlineData("exec:/bin/sh -c id")]
    [InlineData("echo:test")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ffmpeg:something-else")]
    public async Task AnythingOutsideTheAllowlistIsRefused(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error.Should().NotBeNull();
        TestLocaliser.Errors().For(check.Error!).Should().NotBeNullOrWhiteSpace(
            "a refusal that names no resource would render as a bare key");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.50/live")]
    public async Task AnIncompleteAddressIsRefused(string source)
    {
        CameraSourcePolicy policy = Build();

        (await policy.CheckAsync(source, CancellationToken.None)).IsAcceptable.Should().BeFalse();
    }

    /// <summary>
    /// The reason this check exists: Homespool does not make the connection, but it decides what
    /// the sidecar is asked to reach - so a name pointing back at the stack is refused before it is
    /// handed over.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    public async Task AHostResolvingToThisServerIsRefused(string resolvesTo)
    {
        CameraSourcePolicy policy = Build(resolvesTo);

        CameraSourceCheck check = await policy.CheckAsync("rtsp://sneaky.example/live", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        TestLocaliser.Errors().For(check.Error!)
                     .Should().Contain(resolvesTo, "the refusal should say what it resolved to");
    }

    /// <summary>
    /// The same refusal through <c>onvif</c>, which is worth its own test rather than another row
    /// above: .NET has no registered parser for that scheme, so whether it populates
    /// <c>Uri.Host</c> at all is what decides if the check above applies to it or silently passes
    /// everything. An empty host would resolve to nothing and accept a loopback address.
    /// </summary>
    [Fact]
    public async Task AnOnvifHostResolvingToThisServerIsRefused()
    {
        CameraSourcePolicy policy = Build("127.0.0.1");

        CameraSourceCheck check = await policy.CheckAsync("onvif://sneaky.example", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        TestLocaliser.Errors().For(check.Error!)
                     .Should().Contain("sneaky.example", "the host has to be parsed for the check to bite");
    }

    /// <summary>
    /// An unresolvable name is allowed through on purpose. The resolver cannot tell "no such name"
    /// from "DNS is unhappy", and a camera that cannot be resolved cannot be reached either - so the
    /// attempt that follows reports it far more usefully than a refusal here would.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableHostIsNotRefusedOnThatBasis()
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync("rtsp://nowhere.invalid/live", CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// The escape hatch, for a deployment shape this project does not have but cannot rule out.
    /// </summary>
    [Fact]
    public async Task TheCheckCanBeTurnedOff()
    {
        CameraSourcePolicy policy = Build("127.0.0.1", refuseLoopback: false);

        CameraSourceCheck check = await policy.CheckAsync("rtsp://sneaky.example/live", CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    private static CameraSourcePolicy Build(string? resolvesTo = null, bool refuseLoopback = true)
    {
        IHostAddressResolver resolver = Substitute.For<IHostAddressResolver>();

        IReadOnlyList<IPAddress> answer = resolvesTo is null ? [] : [IPAddress.Parse(resolvesTo)];

        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(answer));

        CameraOptions options = new() { RefuseLoopbackAndLinkLocal = refuseLoopback };

        return new CameraSourcePolicy(resolver, Options.Create(options));
    }
}
