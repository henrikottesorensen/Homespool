using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Notifications.WebPush;

namespace Homespool.Host.Test;

/// <summary>
/// Which push endpoints Homespool will post to, and that the connection itself refuses anything but a
/// public address - the two halves of the one place Homespool dials an address somebody else chose.
/// </summary>
public sealed class WebPushEndpointPolicyTests
{
    private static WebPushEndpointPolicy Policy(params string[] additionalHosts)
    {
        return new WebPushEndpointPolicy(TestOptions.Monitor(new WebPushOptions { AdditionalEndpointHosts = [.. additionalHosts] }));
    }

    /// <summary>
    /// Real endpoints, in the shapes the four browser vendors hand out.
    /// </summary>
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/dGVzdDp0ZXN0")]
    [InlineData("https://fcm.googleapis.com/wp/dGVzdDp0ZXN0")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAAABm")]
    [InlineData("https://web.push.apple.com/QGuQyavXutnMPl4_7AbCdE")]
    [InlineData("https://wns2-par02p.notify.windows.com/w/?token=BQYAAAB")]
    [InlineData("https://FCM.googleapis.com/fcm/send/case-does-not-matter")]
    public void EveryMajorPushServiceIsAccepted(string endpoint)
    {
        Policy().Allows(endpoint).Should().BeTrue();
    }

    /// <summary>
    /// Everything a hand-posted form could try instead: another host, another scheme or port, a
    /// credential, a literal address, and names built to look like a listed one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("https://example.com/push")]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/x")]
    [InlineData("https://user:pass@fcm.googleapis.com/fcm/send/x")] // betterleaks:allow - a placeholder, the credential being what is refused
    [InlineData("https://fcm.googleapis.com/fcm/send/x#fragment")]
    [InlineData("https://fcm.googleapis.com.attacker.example/x")]
    [InlineData("https://evilfcm.googleapis.com/x")]
    [InlineData("https://sub.fcm.googleapis.com/x")]
    [InlineData("https://notify.windows.com/x")]
    [InlineData("https://evilnotify.windows.com/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public void AnythingElseIsRefused(string? endpoint)
    {
        Policy().Allows(endpoint).Should().BeFalse();
    }

    [Fact]
    public void AnOverlongEndpointIsRefused()
    {
        string endpoint = "https://fcm.googleapis.com/fcm/send/" + new string('a', Model.Entities.WebPushDestination.EndpointMaxLength);

        Policy().Allows(endpoint).Should().BeFalse();
    }

    [Fact]
    public void AnOperatorCanAddAPushServiceByNameOrByDomain()
    {
        WebPushEndpointPolicy policy = Policy("push.example.net", "*.push.example.org");

        policy.Allows("https://push.example.net/x").Should().BeTrue();
        policy.Allows("https://eu.push.example.org/x").Should().BeTrue();

        policy.Allows("https://push.example.org/x").Should().BeFalse("a wildcard names what is under a domain, not the domain");
        policy.Allows("https://other.example.net/x").Should().BeFalse();
    }

    /// <summary>
    /// Addresses a push service could be at, and every kind it could not - including the forms that
    /// wrap an IPv4 address inside IPv6.
    /// </summary>
    [Theory]
    [InlineData("142.250.74.106", true)]
    [InlineData("2a00:1450:400f:80d::200a", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.1.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("::ffff:10.0.0.5", false)]
    [InlineData("::ffff:142.250.74.106", true)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("64:ff9b::a00:5", false)]
    [InlineData("2002:a00:5::1", false)]
    [InlineData("ff02::1", false)]
    public void OnlyAPublicAddressMayBeConnectedTo(string address, bool isPublic)
    {
        PushAddressGuard.IsPublicAddress(System.Net.IPAddress.Parse(address)).Should().Be(isPublic);
    }

    /// <summary>
    /// The guard in place, on a real handler: a request to a private address fails before any
    /// connection, and says why in a way the channel can tell from a network failure.
    /// </summary>
    [Fact]
    public async Task TheConnectionItselfIsRefusedForAPrivateAddress()
    {
        using SocketsHttpHandler handler = new() { ConnectCallback = PushAddressGuard.ConnectAsync, UseProxy = false };
        using HttpClient client = new(handler);

        HttpRequestException failure = (await FluentActions.Awaiting(() => client.GetAsync(
                                                                          "http://127.0.0.1:9/x",
                                                                          TestContext.Current.CancellationToken))
                                                           .Should().ThrowAsync<HttpRequestException>()).Which;

        failure.InnerException.Should().BeOfType<PushAddressRefusedException>();
    }

    /// <summary>
    /// The same refusal for a name, which is the case the allowlist cannot see: localhost is not on it,
    /// but a listed name could resolve there just as well.
    /// </summary>
    [Fact]
    public async Task ANameResolvingOnlyToPrivateAddressesIsRefusedToo()
    {
        using SocketsHttpHandler handler = new() { ConnectCallback = PushAddressGuard.ConnectAsync, UseProxy = false };
        using HttpClient client = new(handler);

        HttpRequestException failure = (await FluentActions.Awaiting(() => client.GetAsync(
                                                                          "http://localhost:9/x",
                                                                          TestContext.Current.CancellationToken))
                                                           .Should().ThrowAsync<HttpRequestException>()).Which;

        failure.InnerException.Should().BeOfType<PushAddressRefusedException>();
    }
}
