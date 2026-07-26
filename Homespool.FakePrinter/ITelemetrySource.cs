using System;

namespace Homespool.FakePrinter;

/// <summary>
/// Feeds <see cref="FakePrinterClient"/>'s telemetry loop: one whole wire message per call, or null
/// when exhausted (a finite replay). The client sends immediately on connect and then waits
/// <see cref="DelayBeforeNext"/> between sends, matching the firmware's send-then-sleep shape.
/// </summary>
public interface ITelemetrySource
{
    /// <summary>The next message's raw UTF-8 payload, or null when the source has nothing further.</summary>
    byte[]? NextMessage(FakeDevice device);

    /// <summary>How long to wait before the next send - typically a function of the device state.</summary>
    TimeSpan DelayBeforeNext(FakeDevice device);
}
