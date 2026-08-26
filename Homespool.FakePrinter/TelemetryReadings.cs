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
    int? TimeToFilamentChange = null,

    // How many tools this printer reports, which decides whether a "slot" object is emitted at all:
    // firmware sends one only when enabled_tool_cnt() > 1, so a single-tool printer sends nothing and
    // 1 - the default - reproduces the capture printer exactly.
    //
    // Above 1, every full message carries a numbered sub-object per tool, and that changes the write
    // shape rather than just the payload: one TelemetrySlotSample row per tool per sample, so a
    // message costs (1 + Tools) rows instead of 1. Every throughput and buffer-ceiling number
    // measured before this option existed was taken on the single-tool shape.
    int Tools = 1,

    // The active tool, 1-based, clamped into range against Tools when a message is built.
    //
    // ZERO IS A REAL VALUE, not a missing one: firmware renders "no tool picked" as 0 (render.cpp,
    // active_slot's NoTool arm). Set 0 to reproduce the printer a gcode command with no T argument
    // will silently decline to act on.
    //
    // How easily a machine reaches it depends on the machine. An INDX docks the tool after a load or
    // an unload - the tool_change(NoTool) at M701_2.cpp:127-131 and :185-188 is under #if HAS_INDX()
    // - so it rests there. An XL leaves the tool picked, and gets there at power-on or after parking
    // one. Both are worth setting; neither is more correct than the other.
    int ActiveTool = 1,

    // MMU progress code and command character - params.progress_code and a single char, emitted only
    // on MMU builds. Null on a non-MMU printer, which is not the same as present-and-zero: see
    // backlog.md on mmu.enabled, where "cannot have one" and "has one, disabled" are distinct states
    // and absent is what distinguishes them.
    int? MmuState = null,
    string? MmuCommand = null);
