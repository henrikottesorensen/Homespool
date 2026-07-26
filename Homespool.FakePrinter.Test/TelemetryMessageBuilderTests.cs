using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The two telemetry shapes against the capture and render.cpp: the job block only with a job,
/// positions only when not printing, fans/filament only when printing, state always last.
/// </summary>
public class TelemetryMessageBuilderTests
{
    /// <summary>An idle machine's slim shape is just the state - nothing else to say.</summary>
    [Fact]
    public void SlimIdleIsJustTheState()
    {
        FakeDevice device = new();

        using JsonDocument document = JsonDocument.Parse(TelemetryMessageBuilder.BuildSlim(device, new TelemetryReadings()));

        document.RootElement.EnumerateObject().Should().HaveCount(1);
        document.RootElement.GetProperty("state").GetString().Should().Be("IDLE");
    }

    /// <summary>A printing machine's slim shape is the capture's five-field form.</summary>
    [Fact]
    public void SlimPrintingIsTheJobBlockPlusState()
    {
        FakeDevice device = new();
        device.StartPrint(jobId: 301);
        TelemetryReadings readings = new(TimePrinting: 4020, TimeRemaining: 10680, Progress: 25);

        using JsonDocument document = JsonDocument.Parse(TelemetryMessageBuilder.BuildSlim(device, readings));

        document.RootElement.GetProperty("job_id").GetInt32().Should().Be(301);
        document.RootElement.GetProperty("time_printing").GetInt32().Should().Be(4020);
        document.RootElement.GetProperty("time_remaining").GetInt32().Should().Be(10680);
        document.RootElement.GetProperty("progress").GetInt32().Should().Be(25);
        document.RootElement.GetProperty("state").GetString().Should().Be("PRINTING");
    }

    /// <summary>
    /// Printing full telemetry has fans and filament but no positions; idle full telemetry has
    /// positions but no fans - the two groups never co-occur (render.cpp:216-232).
    /// </summary>
    [Fact]
    public void PositionAndFanGroupsNeverCoOccur()
    {
        FakeDevice printing = new();
        printing.StartPrint(jobId: 1);
        FakeDevice idle = new();

        using JsonDocument printingDoc = JsonDocument.Parse(TelemetryMessageBuilder.BuildFull(printing, new TelemetryReadings()));
        using JsonDocument idleDoc = JsonDocument.Parse(TelemetryMessageBuilder.BuildFull(idle, new TelemetryReadings()));

        printingDoc.RootElement.TryGetProperty("fan_extruder", out _).Should().BeTrue();
        printingDoc.RootElement.TryGetProperty("axis_x", out _).Should().BeFalse();

        idleDoc.RootElement.TryGetProperty("axis_x", out _).Should().BeTrue();
        idleDoc.RootElement.TryGetProperty("fan_extruder", out _).Should().BeFalse();
        idleDoc.RootElement.TryGetProperty("filament", out _).Should().BeFalse();
    }

    /// <summary>
    /// <c>filament_change_in</c> is emitted only when a pause is actually scheduled, and in the
    /// firmware's own position - between <c>time_remaining</c> and <c>progress</c>.
    /// </summary>
    /// <remarks>
    /// The field was missing from this builder until 2026-07-27, found by running the real firmware
    /// client (notes/buddy-rig.md) and corroborated by Prusa's own <c>render.cpp</c> "Telemetry -
    /// reduced" expectation. It matters more than its size suggests: the firmware gates it on
    /// <c>time_to_pause</c> (render.cpp:164), so an ordinary print omits it and the committed capture
    /// contains none - meaning this builder is the only way anything exercises the field's path from
    /// wire to database. On a single-tool printer it is the countdown to an M600 colour swap, and the
    /// only predictive signal available for warning a user before the printer stops and waits.
    /// </remarks>
    [Fact]
    public void FilamentChangeCountdownIsEmittedOnlyWhenAPauseIsScheduled()
    {
        // Arrange
        FakeDevice device = new();
        device.StartPrint(jobId: 42);

        // Act
        using JsonDocument without = JsonDocument.Parse(
            TelemetryMessageBuilder.BuildSlim(device, new TelemetryReadings()));
        using JsonDocument with = JsonDocument.Parse(
            TelemetryMessageBuilder.BuildSlim(device, new TelemetryReadings(TimeToFilamentChange: 300)));

        // Assert
        without.RootElement.TryGetProperty("filament_change_in", out _)
            .Should().BeFalse("no scheduled pause means the field is absent, not zero");

        with.RootElement.GetProperty("filament_change_in").GetInt32().Should().Be(300);

        // Explicit array, not the params overload: with a trailing "because" string that overload
        // reads the reason as one more expected element.
        string[] firmwareOrder = ["job_id", "time_printing", "time_remaining", "filament_change_in", "progress", "state"];

        with.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().Equal(firmwareOrder,
                "the field sits between time_remaining and progress, matching the firmware's own order");
    }
}
