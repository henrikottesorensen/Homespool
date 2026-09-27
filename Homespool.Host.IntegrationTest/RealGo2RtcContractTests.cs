using System;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// The sidecar contract, against Homespool's go2rtc image as <c>start-go2rtc.sh</c> runs it.
/// Skipped when no such sidecar answers.
/// </summary>
public sealed class RealGo2RtcContractTests : Go2RtcContract
{
    protected override Uri BaseAddress => Go2RtcFixture.BaseAddress;

    protected override int RtspPort => Go2RtcFixture.RtspPort;

    protected override string Username => Go2RtcFixture.Username;

    protected override string Password => Go2RtcFixture.Password;

    protected override void RequireSidecar()
    {
        Assert.SkipUnless(Go2RtcFixture.IsServing, Go2RtcFixture.NotServing);
    }
}
