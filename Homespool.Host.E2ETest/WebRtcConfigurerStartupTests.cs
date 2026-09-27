using System.Threading.Tasks;

using AwesomeAssertions;

namespace Homespool.Host.E2ETest;

/// <summary>
/// What starting Homespool does to the sidecar's WebRTC configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>A write is not enough on its own, and not always wanted.</b> A candidate written without a
/// restart reads back from the configuration looking applied while being absent from every offer,
/// so a write has to be followed by one. A restart drops everybody watching, so a sidecar already
/// saying the right thing must be left alone - which is only worth asserting once the configuration
/// is shown to have been read.
/// </para>
/// </remarks>
public sealed class WebRtcConfigurerStartupTests : IAsyncLifetime
{
    private const string Candidate = "192.0.2.10:8555";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("webrtc-startup");
    private FakeGo2Rtc _sidecar = null!;
    private HomespoolFactory? _factory;

    public async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _sidecar.DisposeAsync();

        _scratch.Dispose();
    }

    /// <summary>
    /// A sidecar that does not advertise the address is given it, with STUN written off, and
    /// restarted so the address takes effect.
    /// </summary>
    [Fact]
    public void ASidecarWithoutTheAddressIsGivenItAndRestarted()
    {
        Start(stunEnabled: false);

        _sidecar.ConfigWrites.Should().ContainSingle()
                .Which.Should().Contain(Candidate)
                .And.Contain("\"ice_servers\":[]", "left out, go2rtc's own default would ask a public STUN server");
        _sidecar.Restarts.Should().Be(1, "a candidate is not in any offer until the sidecar restarts");
    }

    /// <summary>
    /// With STUN switched on, the written configuration names the STUN server.
    /// </summary>
    [Fact]
    public void SwitchingStunOnWritesTheStunServer()
    {
        Start(stunEnabled: true);

        _sidecar.ConfigWrites.Should().ContainSingle()
                .Which.Should().Contain("stun:stun.l.google.com:19302");
        _sidecar.Restarts.Should().Be(1);
    }

    /// <summary>
    /// A sidecar already advertising the address, with STUN as configured, is read and then left
    /// alone - neither rewritten nor restarted under anybody watching.
    /// </summary>
    [Fact]
    public void ASidecarAlreadyAdvertisingTheAddressIsLeftAlone()
    {
        _sidecar.Config = $"webrtc:\n  candidates:\n    - {Candidate}\n  ice_servers: []\n";

        Start(stunEnabled: false);

        _sidecar.Requests.Should().Contain("GET /api/config", "the configuration must have been read, or leaving it alone proves nothing");
        _sidecar.ConfigWrites.Should().BeEmpty();
        _sidecar.Restarts.Should().Be(0);
    }

    /// <summary>
    /// Starts a host against the sidecar. The configurer is a hosted service, so it has finished by
    /// the time the server exists.
    /// </summary>
    private void Start(bool stunEnabled)
    {
        _factory = new HomespoolFactory(_scratch);
        _sidecar.ApplyTo(_factory);
        _factory.ConfigurationOverrides["Cameras:WebRtcCandidate"] = Candidate;
        _factory.ConfigurationOverrides["Cameras:WebRtcStunEnabled"] = stunEnabled ? "true" : "false";

        _ = _factory.Server;
    }
}
