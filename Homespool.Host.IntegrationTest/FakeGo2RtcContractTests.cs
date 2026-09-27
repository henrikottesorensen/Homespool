using System;
using System.Threading.Tasks;

using Homespool.Host.E2ETest;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// The sidecar contract, against the fake every E2E camera test stands on. Always runs.
/// </summary>
public sealed class FakeGo2RtcContractTests : Go2RtcContract
{
    private FakeGo2Rtc _sidecar = null!;

    protected override Uri BaseAddress => _sidecar.BaseAddress;

    protected override int RtspPort => _sidecar.RtspPort;

    protected override string Username => FakeGo2Rtc.Username;

    protected override string Password => FakeGo2Rtc.Password;

    public override async ValueTask InitializeAsync()
    {
        _sidecar = await FakeGo2Rtc.StartAsync();

        // The fake knows a camera by its source, so the contract's two test patterns are declared
        // as what the real ffmpeg makes of them. The TEST-NET address is left undeclared, which is
        // how the fake spells a camera that is not there.
        _sidecar.AddCamera(H264Source, FakeCamera.H264);
        _sidecar.AddCamera(JpegSource, FakeCamera.Jpeg);

        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _sidecar.DisposeAsync();
    }
}
