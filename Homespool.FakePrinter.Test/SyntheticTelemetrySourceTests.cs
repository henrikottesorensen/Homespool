using System;
using System.Text.Json;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The synthetic source's filament odometer: still through a print's warm-up, moving after it, and
/// sent as firmware sends it.
/// </summary>
/// <remarks>
/// What the fake is reproducing is a hardware finding: a printer reports <c>PRINTING</c> with the
/// nozzle cold, and the odometer first rising is the only sign that plastic has started to move.
/// Driven by <see cref="FakeTimeProvider"/>, and with every message full, so the reading is on each one.
/// </remarks>
public class SyntheticTelemetrySourceTests
{
    private readonly FakeTimeProvider _clock = new();

    /// <summary>Nothing moves during the warm-up; afterwards the odometer climbs at the configured rate.</summary>
    [Fact]
    public void TheOdometerIsStillThroughTheWarmUpAndClimbsAfterIt()
    {
        // Arrange
        FakeDevice device = new();
        device.StartPrint(jobId: 7);
        SyntheticTelemetrySource source = NewSource();
        double opening = FilamentIn(source.NextMessage(device));

        // Act
        _clock.Advance(TimeSpan.FromSeconds(55));
        double warming = FilamentIn(source.NextMessage(device));

        _clock.Advance(TimeSpan.FromSeconds(15));
        double printing = FilamentIn(source.NextMessage(device));

        // Assert
        warming.Should().Be(opening, "a print homing, probing and heating extrudes nothing");
        printing.Should().BeApproximately(opening + (10 * 2.0), 0.1, "ten seconds past the warm-up at 2 mm/s");
    }

    /// <summary>A pause stops the odometer and does not eat into the warm-up; a new print starts its own.</summary>
    [Fact]
    public void APauseDoesNotCountAndANewPrintWarmsUpAgain()
    {
        // Arrange
        FakeDevice device = new();
        device.StartPrint(jobId: 7);
        SyntheticTelemetrySource source = NewSource();
        source.NextMessage(device);

        _clock.Advance(TimeSpan.FromSeconds(30));
        source.NextMessage(device);
        device.TryPause().Should().BeTrue();

        // Act - paused for a long time, then resumed and printed past the warm-up
        _clock.Advance(TimeSpan.FromMinutes(10));
        double paused = FilamentIn(source.NextMessage(device));
        device.TryResume().Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(40));
        double resumed = FilamentIn(source.NextMessage(device));

        device.FinishPrint().Should().BeTrue();
        device.TrySetReady().Should().BeTrue();
        device.StartPrint(jobId: 8);
        source.NextMessage(device);

        _clock.Advance(TimeSpan.FromSeconds(30));
        double secondWarming = FilamentIn(source.NextMessage(device));

        // Assert
        resumed.Should().BeApproximately(paused + (10 * 2.0), 0.1,
                                         "thirty seconds before the pause and forty after make seventy, ten past the warm-up");
        secondWarming.Should().Be(resumed, "the second print is thirty seconds into its own warm-up");
    }

    private SyntheticTelemetrySource NewSource()
    {
        return new SyntheticTelemetrySource
        {
            Clock = _clock,
            WarmUp = TimeSpan.FromSeconds(60),
            ExtrusionRate = 2.0,
            FullShapeEvery = 1,
        };
    }

    private static double FilamentIn(byte[]? message)
    {
        message.Should().NotBeNull();

        using JsonDocument document = JsonDocument.Parse(message);

        return document.RootElement.GetProperty("filament").GetDouble();
    }
}
