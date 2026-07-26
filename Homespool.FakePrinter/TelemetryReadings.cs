namespace Homespool.FakePrinter;

/// <summary>
/// Live analog values for the full telemetry shape - the numbers a synthetic source drifts per
/// send so consecutive messages look like a real machine rather than a stuck one. Defaults are a
/// cold, idle printer.
/// </summary>
public sealed record TelemetryReadings(
    double NozzleTemperature = 25.0,
    double BedTemperature = 24.0,
    double TargetNozzle = 0.0,
    double TargetBed = 0.0,
    int Speed = 100,
    int Flow = 100,
    string Material = "PLA",
    double AxisZ = 0.0,
    int FanExtruder = 0,
    int FanPrint = 0,
    int TimePrinting = 0,
    int TimeRemaining = 0,
    int Progress = 0);
