using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The transition table against <c>MarlinPrinter::job_control</c> (marlin_printer.cpp:442) and
/// <c>set_ready</c>/<c>set_idle</c> (:574-586) - which state each command is legal from decides
/// FINISHED vs REJECTED on the wire.
/// </summary>
public class FakeDeviceTests
{
    /// <summary>Every state the device can be in, so the theories below cannot miss one.</summary>
    public static TheoryData<DeviceState> AllDeviceStates()
    {
        TheoryData<DeviceState> data = [];

        foreach (DeviceState state in System.Enum.GetValues<DeviceState>())
        {
            data.Add(state);
        }

        return data;
    }

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

    /// <summary>Un-readying never fails, and does nothing to a machine that is not ready.</summary>
    [Fact]
    public void CancelReadyAlwaysWorks()
    {
        FakeDevice device = new();

        device.TrySetReady().Should().BeTrue();
        device.State.Should().Be(DeviceState.Ready);

        device.CancelReady();
        device.State.Should().Be(DeviceState.Idle);

        device.StartPrint(jobId: 1);

        // CancelReady on a non-ready machine is a no-op, not an error (the firmware asserts
        // set_ready(false) cannot fail).
        device.CancelReady();
        device.State.Should().Be(DeviceState.Printing);
    }

    /// <summary>
    /// <b>Set ready is accepted from exactly the four states <c>remote_print_ready</c> names</b>
    /// (printer_state.cpp:561-577), which is the same gate <c>START_PRINT</c> passes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exhaustive because this fake was wrong here, and wrong in the expensive direction.</b> It
    /// took the flag only from <c>Idle</c>, so it refused what hardware accepts from
    /// <c>Finished</c> and <c>Stopped</c> - which is how a parked print is released, and therefore
    /// the single most ordinary way a real queue resumes. A test of that path would have failed
    /// against the double while working perfectly against a printer.
    /// </para>
    /// <para>
    /// <c>Undefined</c> and <c>Unknown</c> are in the theory rather than excluded: neither is a
    /// state firmware offers work from, and driving them from the enum is what stops a member added
    /// later from picking up an answer nobody chose.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDeviceStates))]
    public void ReadyIsAcceptedFromExactlyTheStatesFirmwareAcceptsItFrom(DeviceState state)
    {
        FakeDevice device = new();

        device.ForceState(state);

        bool expected = state is DeviceState.Idle or DeviceState.Ready
                              or DeviceState.Stopped or DeviceState.Finished;

        device.TrySetReady().Should().Be(expected);

        if (expected)
        {
            device.State.Should().Be(DeviceState.Ready);
        }
        else
        {
            device.State.Should().Be(state, "a refused command changes nothing");
        }
    }

    /// <summary>
    /// Readying off the finished screen drops the job, exactly as setting idle from there does.
    /// </summary>
    /// <remarks>
    /// Firmware's <c>has_job</c> is false in <c>Ready</c> (printer_state.cpp:580-592), so it stops
    /// sending the job block altogether. A fake that kept the id would put a printer on the wire
    /// claiming to be free for work and to have work in hand at once - which is the shape a server
    /// reads as an unattributed print.
    /// </remarks>
    [Fact]
    public void ReadyingOffTheFinishedScreenDropsTheJob()
    {
        FakeDevice device = new();

        device.StartPrint(jobId: 7, path: "/usb/A~1.BGC");
        device.FinishPrint().Should().BeTrue();
        device.JobId.Should().Be(7, "a finished print is still named on its own screen");

        device.TrySetReady().Should().BeTrue("the flag overrides the finished screen");

        device.State.Should().Be(DeviceState.Ready);
        device.JobId.Should().BeNull();
        device.JobPath.Should().BeNull();
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
