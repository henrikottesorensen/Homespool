using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Homespool.Host.Cameras;
using Homespool.Host.Certificates;

namespace Homespool.Host.Test;

/// <summary>
/// What Homespool will and will not ask the stream server to reach on its behalf.
/// </summary>
/// <remarks>
/// The resolver is substituted, so "this name points at the server itself" is producible without a
/// DNS server - which is the whole reason <see cref="IHostAddressResolver"/> is an interface. The
/// machine is substituted for the same reason: see <see cref="ILocalMachine"/>.
/// </remarks>
public class CameraSourcePolicyTests
{
    [Theory]
    [InlineData("rtsp://192.168.13.217/live")]
    [InlineData("rtsps://cam.example/live")]
    [InlineData("http://192.168.1.50/snapshot.jpg")]
    [InlineData("https://cam.example/snapshot")]
    [InlineData("rtmp://192.168.1.50/stream")]
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
    /// An <c>onvif</c> address is refused however ordinary its host, because the host is not what
    /// the sidecar ends up reading: it asks the device for a stream address and opens whatever
    /// comes back, unchecked - an <c>ffmpeg:</c> source or the sidecar's own loopback RTSP port
    /// included. Its own test rather than rows above, since the host here resolves to an ordinary
    /// camera and the refusal must not depend on it.
    /// </summary>
    [Theory]
    [InlineData("onvif://192.168.1.50")]
    [InlineData("onvif://user:pass@cam.example")]
    public async Task AnOnvifAddressIsRefusedWhereverItPoints(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourceScheme");
    }

    /// <summary>
    /// An unresolvable name is refused at a save. The sidecar resolves the name again when it dials,
    /// so a name this lookup cannot see is one the check has said nothing about - and a name that
    /// fails here and succeeds there is exactly what a rebinding attacker offers first.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableHostIsRefusedAtASave()
    {
        CameraSourcePolicy policy = Build(unresolvable: true);

        CameraSourceCheck check = await policy.CheckAsync("rtsp://nowhere.invalid/live", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourceUnresolvable");
    }

    /// <summary>
    /// The reconciler asks the other way round: at start-up nobody can retry, DNS may still be
    /// waking up, and a name that resolves to nothing reaches nothing.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableHostIsKeptWhenTheCallerSaysSo()
    {
        CameraSourcePolicy policy = Build(unresolvable: true);

        CameraSourceCheck check = await policy.CheckAsync("rtsp://nowhere.invalid/live", acceptUnresolvable: true, CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// A name is resolved in its ASCII form. The resolver answers only that form, so asking about
    /// the Unicode spelling would turn every internationalised camera name into an unresolvable one.
    /// </summary>
    [Fact]
    public async Task AnInternationalisedNameIsResolvedInItsAsciiForm()
    {
        IHostAddressResolver resolver = Substitute.For<IHostAddressResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.113.10")]));

        CameraSourcePolicy policy = Build(resolver: resolver);

        CameraSourceCheck check = await policy.CheckAsync("rtsp://kælder-kamera.example/live", CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
        await resolver.Received(1).ResolveAsync("xn--klder-kamera-6cb.example", Arg.Any<CancellationToken>());
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

    /// <summary>
    /// The sidecar, by the name the deployment was configured to reach it on. This is the address
    /// that turns a camera source into a way to drive go2rtc's own API.
    /// </summary>
    [Theory]
    [InlineData("http://go2rtc:1984/api/stream.mjpeg?src=exec:whoami")]
    [InlineData("http://GO2RTC:1984/api/streams")]
    [InlineData("rtsp://go2rtc/live")]
    public async Task TheStreamServerIsNotACamera(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeFalse("the sidecar's own API is the target this check exists for");
        check.Error!.Key.Should().Be("Cameras_SourceIsThisDeployment");
    }

    /// <summary>
    /// A fragment naming another address is refused, whatever the host in front of it is.
    /// </summary>
    /// <remarks>
    /// The sidecar reads what follows <c>#</c> as options, and <c>transport=</c> is an address it
    /// dials instead of the host this check resolved - with the source's credential. So a fragment
    /// carrying an address makes every check here answer a question about the wrong host.
    /// </remarks>
    [Theory]
    [InlineData("rtsp://admin:secret@camera.example/live#transport=ws://attacker.example/x")]
    [InlineData("rtsp://camera.example/live#transport=WSS://attacker.example/x")]
    [InlineData("http://camera.example/snapshot.jpg#raw=http://127.0.0.1:1984/api")]
    public async Task AFragmentNamingAnAddressIsRefused(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeFalse("the address the sidecar would dial is not the one checked here");
        check.Error!.Key.Should().Be("Cameras_SourceFragmentAddress");
    }

    /// <summary>A fragment that names no address is left alone - it is the sidecar's own vocabulary.</summary>
    [Theory]
    [InlineData("rtsp://camera.example/live#backchannel=0")]
    [InlineData("rtsp://camera.example/live#media=video")]
    public async Task AFragmentThatNamesNoAddressIsAccepted(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// A source naming one of the sidecar's own settings is refused, wherever in the source it sits.
    /// </summary>
    /// <remarks>
    /// go2rtc expands <c>${NAME}</c> in its configuration file each time it loads it, from its
    /// environment and its credentials directory - which is where the API password is mounted. A
    /// source is written into that file as typed, so the placeholder becomes the secret, sent to
    /// whichever host the rest of the source names. The resolver answers with a public address, so
    /// nothing but the placeholder is left to refuse these.
    /// </remarks>
    [Theory]
    [InlineData("http://attacker.example/${GO2RTC_PASSWORD}")]
    [InlineData("rtsp://camera.example/live?token=${GO2RTC_PASSWORD}")]
    [InlineData("rtsp://admin:${GO2RTC_PASSWORD}@camera.example/live")]
    [InlineData("http://camera.example/${../../etc/hostname}")]
    [InlineData("rtsp://camera.example/live#media=${X}")]
    [InlineData("ffmpeg:device?video=/dev/v4l/by-id/${X}&input_format=mjpeg")]
    public async Task ASourceNamingASidecarSettingIsRefused(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeFalse("the sidecar would replace the placeholder with its own secret");
        check.Error!.Key.Should().Be("Cameras_SourcePlaceholder");
    }

    /// <summary>Not an address rule, so turning the address rules off does not turn it off.</summary>
    [Fact]
    public async Task ASidecarSettingIsRefusedWithTheAddressChecksOff()
    {
        CameraSourcePolicy policy = Build(refuseLoopback: false);

        CameraSourceCheck check = await policy.CheckAsync("http://attacker.example/${GO2RTC_PASSWORD}", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourcePlaceholder");
    }

    /// <summary>
    /// A dollar sign on its own is not a placeholder, and passwords have them - as does a brace the
    /// user percent-encoded, which the sidecar decodes only after its configuration is loaded.
    /// </summary>
    [Theory]
    [InlineData("rtsp://admin:pa$$word@camera.example/live")]
    [InlineData("rtsp://admin:pa%24%7Bx%7D@camera.example/live")]
    public async Task ADollarThatIsNotAPlaceholderIsAccepted(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// A source holding a character nobody can see is refused, wherever in the source it sits - the
    /// password included, which is never shown again once saved.
    /// </summary>
    /// <remarks>
    /// The line and paragraph separators are the two that did damage: go2rtc writes them into its
    /// configuration file unescaped and reads the file back with a parser that breaks lines at them,
    /// so every camera after one sat a line out of place. The resolver answers with a public address,
    /// so nothing but the character is left to refuse these.
    /// </remarks>
    [Theory]
    [InlineData("rtsp://camera.example/live\u2028x")]
    [InlineData("rtsp://camera.example/live?x=\u2029y")]
    [InlineData("rtsp://admin:pass\u2028word@camera.example/live")]
    [InlineData("rtsp://ad\u2029min:password@camera.example/live")]
    [InlineData("rtsp://camera.example/live\u0085x")]
    [InlineData("rtsp://camera.example/li\u202Eve")]
    [InlineData("rtsp://camera.example/li\u200Bve")]
    [InlineData("rtsp://camera.example/li\tve")]
    [InlineData("ffmpeg:device?video=/dev/v4l/by-id/usb-camera\u2028&input_format=mjpeg")]
    public async Task ASourceWithAnUnprintableCharacterIsRefused(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeFalse("nobody can see the character, and the sidecar's file breaks a line at some of them");
        check.Error!.Key.Should().Be("Cameras_SourceUnprintable");
    }

    /// <summary>Not an address rule, so turning the address rules off does not turn it off.</summary>
    [Fact]
    public async Task AnUnprintableCharacterIsRefusedWithTheAddressChecksOff()
    {
        CameraSourcePolicy policy = Build(refuseLoopback: false);

        CameraSourceCheck check = await policy.CheckAsync("rtsp://camera.example/live\u2028x", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourceUnprintable");
    }

    /// <summary>
    /// What is merely not English is printable, and a separator around the source rather than in it
    /// is whitespace, trimmed before the source is checked or stored.
    /// </summary>
    [Theory]
    [InlineData("rtsp://admin:pæssørd@camera.example/stue")]
    [InlineData("rtsp://camera.example/live\u2028")]
    public async Task APrintableSourceIsAccepted(string source)
    {
        CameraSourcePolicy policy = Build();

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// Homespool's container identity - the name it answers to inside the Compose network.
    /// </summary>
    [Fact]
    public async Task ThisContainerIsNotACamera()
    {
        CameraSourcePolicy policy = Build(hostName: "homespool");

        CameraSourceCheck check = await policy.CheckAsync("http://homespool:8080/api/v1/printers", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourceIsThisDeployment");
    }

    /// <summary>
    /// Homespool's outer identity - the address printers are told to reach it on, which is the one
    /// public name the application is actually given.
    /// </summary>
    [Theory]
    [InlineData("homespool.example", "https://homespool.example/")]
    [InlineData("homespool.example", "https://HOMESPOOL.EXAMPLE:15443/p/ws")]
    public async Task TheConfiguredPrinterAddressIsNotACamera(string printerHost, string source)
    {
        CameraSourcePolicy policy = Build(printerHost: printerHost);

        CameraSourceCheck check = await policy.CheckAsync(source, CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourceIsThisDeployment");
    }

    /// <summary>
    /// A short name and its search-domain form are the same host, so refusing only the spelling we
    /// happened to store would be a refusal somebody could step around by typing the other.
    /// </summary>
    [Theory]
    [InlineData("homespool", "homespool.local")]
    [InlineData("homespool.local", "homespool")]
    public void AShortNameAndItsQualifiedFormAreTheSameHost(string configured, string typed)
    {
        CameraSourcePolicy.NamesThisDeployment(typed, [configured]).Should().BeTrue();
    }

    [Fact]
    public void ASimilarNameIsNotTheSameHost()
    {
        CameraSourcePolicy.NamesThisDeployment("homespool-cam.local", ["homespool"]).Should().BeFalse(
            "a camera named after the server is still a camera");
    }

    /// <summary>
    /// An address inside the deployment's own container range, which catches every service in the
    /// stack - including one this check has never been told about.
    /// </summary>
    [Fact]
    public async Task AnAddressInsideTheContainerNetworkIsRefused()
    {
        CameraSourcePolicy policy = Build(resolvesTo: "172.28.0.3", containerNetwork: "172.28.0.0/16");

        CameraSourceCheck check = await policy.CheckAsync("rtsp://camera.example/live", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourceIsThisServer");
    }

    /// <summary>
    /// The same address with no container range configured - the deployment on a 172.16/12 LAN that
    /// was told to empty the list. It is allowed, and that is the documented cost of emptying it.
    /// </summary>
    [Fact]
    public async Task AnAddressOutsideTheConfiguredRangesIsStillACamera()
    {
        CameraSourcePolicy policy = Build(resolvesTo: "172.28.0.3");

        CameraSourceCheck check = await policy.CheckAsync("rtsp://camera.example/live", CancellationToken.None);

        check.IsAcceptable.Should().BeTrue();
    }

    /// <summary>
    /// An address this machine holds is this server, whatever range it is in - the deployment with no
    /// container range configured at all, where the interface addresses are the only thing that
    /// catches it.
    /// </summary>
    [Fact]
    public async Task AnAddressThisMachineHoldsIsRefused()
    {
        CameraSourcePolicy policy = Build(resolvesTo: "192.168.1.20", ownAddress: "192.168.1.20");

        CameraSourceCheck check = await policy.CheckAsync("rtsp://camera.example/live", CancellationToken.None);

        check.IsAcceptable.Should().BeFalse();
        check.Error!.Key.Should().Be("Cameras_SourceIsThisServer");
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("::ffff:192.168.1.20", "192.168.1.20")]
    [InlineData("192.168.1.20", "::ffff:192.168.1.20")]
    [InlineData("fd00::20", "fd00::20")]
    public void AnAddressThisMachineHoldsIsInsideTheDeployment(string address, string own)
    {
        CameraSourcePolicy.IsInsideThisDeployment(IPAddress.Parse(address), [], [IPAddress.Parse(own)])
                          .Should().BeTrue();
    }

    [Fact]
    public void AnAddressThisMachineDoesNotHoldIsOutsideTheDeployment()
    {
        CameraSourcePolicy.IsInsideThisDeployment(IPAddress.Parse("192.168.1.21"), [], [IPAddress.Parse("192.168.1.20")])
                          .Should().BeFalse();
    }

    /// <summary>
    /// The application reads the machine it runs on. A registration answering nothing would narrow
    /// the check without failing anything, so nothing else would notice it.
    /// </summary>
    [Fact]
    public void TheApplicationRegistersThePlatformsAnswer()
    {
        ServiceCollection services = [];

        services.AddCameras(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(ILocalMachine))
                .Which.ImplementationType.Should().Be<PlatformLocalMachine>();
    }

    /// <summary>
    /// 0.0.0.0 is not loopback, so it passed the reachability check and reached the local host
    /// anyway on Linux.
    /// </summary>
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void TheUnspecifiedAddressIsNotReachable(string address)
    {
        CameraSourcePolicy.IsReachableAddress(IPAddress.Parse(address)).Should().BeFalse();
    }

    /// <summary>
    /// A policy over a resolver that answers every name with <paramref name="resolvesTo"/> - a
    /// documentation-range address by default, so an ordinary camera name is an ordinary camera -
    /// or with nothing at all when <paramref name="unresolvable"/>.
    /// </summary>
    /// <remarks>
    /// The machine is a stand-in too, with no name and no addresses unless a test gives it
    /// <paramref name="hostName"/> or <paramref name="ownAddress"/>, so no test here depends on what
    /// the machine running it is called or which addresses it holds.
    /// </remarks>
    internal static CameraSourcePolicy Build(string? resolvesTo = "203.0.113.10",
                                             bool refuseLoopback = true,
                                             string? containerNetwork = null,
                                             string? printerHost = null,
                                             bool unresolvable = false,
                                             IHostAddressResolver? resolver = null,
                                             string? hostName = null,
                                             string? ownAddress = null)
    {
        if (resolver is null)
        {
            resolver = Substitute.For<IHostAddressResolver>();

            IReadOnlyList<IPAddress> answer = unresolvable || resolvesTo is null ? [] : [IPAddress.Parse(resolvesTo)];

            resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(answer));
        }

        CameraOptions options = new() { RefuseLoopbackAndLinkLocal = refuseLoopback };

        CertificateOptions certificates = new()
        {
            ContainerNetworks = containerNetwork is null ? [] : [containerNetwork],
        };

        Homespool.Host.PrusaConnect.PrusaConnectOptions connect = new()
        {
            PrinterHost = printerHost ?? string.Empty,
        };

        ILocalMachine machine = Substitute.For<ILocalMachine>();
        machine.HostName().Returns(hostName);
        machine.Addresses().Returns(ownAddress is null ? [] : [IPAddress.Parse(ownAddress)]);

        return new CameraSourcePolicy(resolver,
                                      machine,
                                      TestOptions.Monitor(options),
                                      TestOptions.Monitor(certificates),
                                      TestOptions.Monitor(connect));
    }
}
