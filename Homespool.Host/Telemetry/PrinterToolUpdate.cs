namespace Homespool.Host.Telemetry;

/// <summary>
/// What an <c>INFO</c> said about one tool's fitted hardware - the subset
/// <see cref="TelemetryWriter"/> upserts into <c>PrinterTool</c> at flush time.
/// </summary>
/// <param name="ToolNumber">1-based, exactly as keyed on the wire.</param>
/// <param name="NozzleDiameter">
/// Millimetres; null when unreported, zero never (a literal 0.0 mm nozzle does not exist and the
/// edge treats it as unreported, matching the top-level field).
/// </param>
/// <param name="Hardened">Whether the nozzle is hardened, and so usable with abrasive filament.</param>
/// <param name="HighFlow">Whether the hotend is a high-flow one.</param>
/// <param name="Material">
/// The loaded material, or null where the wire carried its <c>"---"</c> sentinel for none.
/// </param>
public sealed record PrinterToolUpdate(int ToolNumber,
                                       float? NozzleDiameter,
                                       bool Hardened,
                                       bool HighFlow,
                                       string? Material);
