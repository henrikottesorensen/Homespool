using System;

using AwesomeAssertions;

using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// Which stretch of time the temperature graph covers, which is a decision rather than a constant.
/// </summary>
public sealed class TemperatureWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);

    /// <summary>An idle printer gets the fixed window - there is no job to follow.</summary>
    [Fact]
    public void NoJobTakesTheIdleWindow()
    {
        (DateTimeOffset from, DateTimeOffset to) = TemperatureWindow.For(new PrinterLiveState(), Now);

        to.Should().Be(Now);
        (to - from).Should().Be(TemperatureWindow.Idle);
    }

    /// <summary>A printer that has never reported is the same case, not a failure.</summary>
    [Fact]
    public void NoLiveStateTakesTheIdleWindow()
    {
        (DateTimeOffset from, DateTimeOffset _) = TemperatureWindow.For(null, Now);

        (Now - from).Should().Be(TemperatureWindow.Idle);
    }

    /// <summary>
    /// A running job sets the window, so the graph starts at the heat-up rather than at an arbitrary
    /// hour ago.
    /// </summary>
    [Fact]
    public void ARunningJobSetsTheWindow()
    {
        (DateTimeOffset from, DateTimeOffset _) = TemperatureWindow.For(
            new PrinterLiveState { Status = PrinterStatus.Printing, TimePrinting = (int)TimeSpan.FromHours(3).TotalSeconds },
            Now);

        (Now - from).Should().Be(TimeSpan.FromHours(3));
    }

    /// <summary>
    /// A print that started a moment ago still gets a graph with something on it - and the minutes
    /// before it are where the heat-up is.
    /// </summary>
    [Fact]
    public void AJustStartedJobStillGetsTheMinimumWindow()
    {
        (DateTimeOffset from, DateTimeOffset _) = TemperatureWindow.For(
            new PrinterLiveState { Status = PrinterStatus.Printing, TimePrinting = 20 },
            Now);

        (Now - from).Should().Be(TemperatureWindow.Minimum);
    }

    /// <summary>
    /// A multi-day print is capped, because retention keeps a fortnight and one page refresh should
    /// not be free to aggregate all of it.
    /// </summary>
    [Fact]
    public void AVeryLongJobIsCapped()
    {
        (DateTimeOffset from, DateTimeOffset _) = TemperatureWindow.For(
            new PrinterLiveState { Status = PrinterStatus.Printing, TimePrinting = (int)TimeSpan.FromDays(4).TotalSeconds },
            Now);

        (Now - from).Should().Be(TemperatureWindow.Maximum);
    }

    /// <summary>
    /// Zero seconds printing is not a job. Firmware clears the whole job block when a print ends, so
    /// a stale zero should not scope the graph to nothing.
    /// </summary>
    [Fact]
    public void ZeroElapsedIsNotAJob()
    {
        (DateTimeOffset from, DateTimeOffset _) = TemperatureWindow.For(
            new PrinterLiveState { Status = PrinterStatus.Finished, TimePrinting = 0 },
            Now);

        (Now - from).Should().Be(TemperatureWindow.Idle);
    }
}
