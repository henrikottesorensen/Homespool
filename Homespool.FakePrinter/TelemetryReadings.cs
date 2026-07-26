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
    int Progress = 0,

    // Seconds until the next scheduled pause or filament change (an M600/M601 - on a single-tool
    // printer, typically a mid-print colour swap). Null when none is scheduled, which is the ordinary
    // case: the firmware emits filament_change_in only while time_to_pause is valid
    // (render.cpp:164), so most real prints never carry it - which is why the committed capture holds
    // none, and why this builder is the only thing that can exercise the field's path.
    //
    // A plain comment, not ///: an XML doc comment cannot sit on a positional record parameter
    // (CS1587), and documenting one parameter via <param> would oblige all fourteen (CS1573).
    int? TimeToFilamentChange = null);
