using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The transition table against <c>MarlinPrinter::job_control</c> (marlin_printer.cpp:442) and
/// <c>set_ready</c>/<c>set_idle</c> (:574-586) - which state each command is legal from decides
/// FINISHED vs REJECTED on the wire.
/// </summary>
public class FakeDeviceTests
{
    /// <summary>Pause is legal only while printing.</summary>
    [Fact]
    public void PauseOnlyWorksWhilePrinting()
    {
        FakeDevice device = new();

        device.TryPause().Should().BeFalse("an idle machine has no print to pause");

        device.StartPrint(jobId: 1);

        device.TryPause().Should().BeTrue();
        device.State.Should().Be(DeviceState.Paused);
        device.JobId.Should().Be(1, "pausing does not end the job");
    }

    /// <summary>Resume is legal only while paused.</summary>
    [Fact]
    public void ResumeOnlyWorksWhilePaused()
    {
        FakeDevice device = new();
        device.StartPrint(jobId: 1);

        device.TryResume().Should().BeFalse("a printing machine has nothing to resume");

        device.TryPause();

        device.TryResume().Should().BeTrue();
        device.State.Should().Be(DeviceState.Printing);
    }

    /// <summary>Stop is legal from Printing, Paused and Attention, and ends the job.</summary>
    [Theory]
    [InlineData(DeviceState.Printing)]
    [InlineData(DeviceState.Paused)]
    [InlineData(DeviceState.Attention)]
    public void StopWorksFromTheThreeJobStates(DeviceState from)
    {
        FakeDevice device = new();
        device.StartPrint(jobId: 1);
        device.ForceState(from);

        device.TryStop().Should().BeTrue();
        device.State.Should().Be(DeviceState.Stopped);
        device.JobId.Should().BeNull("stopping ends the job");
    }

    /// <summary>Stop from a jobless state is refused.</summary>
    [Fact]
    public void StopFromIdleIsRefused()
    {
        FakeDevice device = new();

        device.TryStop().Should().BeFalse();
        device.State.Should().Be(DeviceState.Idle);
    }

    /// <summary>Ready is only reachable from Idle, and un-readying never fails.</summary>
    [Fact]
    public void ReadyIsOnlyReachableFromIdleAndCancelAlwaysWorks()
    {
        FakeDevice device = new();

        device.TrySetReady().Should().BeTrue();
        device.State.Should().Be(DeviceState.Ready);

        device.CancelReady();
        device.State.Should().Be(DeviceState.Idle);

        device.StartPrint(jobId: 1);
        device.TrySetReady().Should().BeFalse("a printing machine can't be marked ready");

        // CancelReady on a non-ready machine is a no-op, not an error (the firmware asserts
        // set_ready(false) cannot fail).
        device.CancelReady();
        device.State.Should().Be(DeviceState.Printing);
    }

    /// <summary>
    /// Idle is only reachable from the Finished/Stopped screens - the check the real MK3.5
    /// demonstrated by rejecting "Can't set idle now" mid-session on 2026-07-24.
    /// </summary>
    [Fact]
    public void IdleIsOnlyReachableFromFinishedOrStopped()
    {
        FakeDevice device = new();

        device.TrySetIdle().Should().BeFalse("already idle is not a legal source state");

        device.StartPrint(jobId: 1);
        device.TryStop();

        device.TrySetIdle().Should().BeTrue();
        device.State.Should().Be(DeviceState.Idle);
    }
}
