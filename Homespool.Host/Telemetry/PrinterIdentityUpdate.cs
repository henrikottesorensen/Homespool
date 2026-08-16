namespace Homespool.Host.Telemetry;

/// <summary>
/// What an identity report said about the machine itself - the subset
/// <see cref="TelemetryWriter"/> applies to the <c>Printer</c> row at flush time. Null means the
/// report did not say; the writer's own rules (firmware and model overwrite, a serial only fills
/// an empty column) stay in the writer, because the fill-once rule needs the database.
/// </summary>
/// <param name="Firmware">Firmware version, e.g. <c>6.6.0+14924</c>.</param>
/// <param name="Model">The printer's model designation, e.g. <c>MK3.5</c>.</param>
/// <param name="NozzleDiameter">Fitted nozzle in millimetres; null when unreported, zero never
/// (a literal 0.0 mm nozzle does not exist and edges treat it as unreported).</param>
/// <param name="HasMmu">Whether an MMU is present and enabled; null when the report carried no
/// MMU block at all, which must not clear a stored true.</param>
/// <param name="SerialNumber">The machine's serial number.</param>
public sealed record PrinterIdentityUpdate(string? Firmware,
                                           string? Model,
                                           float? NozzleDiameter,
                                           bool? HasMmu,
                                           string? SerialNumber);
