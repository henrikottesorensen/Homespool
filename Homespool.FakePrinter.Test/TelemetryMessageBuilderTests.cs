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
}
