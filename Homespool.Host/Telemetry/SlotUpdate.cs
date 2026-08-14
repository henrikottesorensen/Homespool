namespace Homespool.Host.Telemetry;

/// <summary>
/// One tool/MMU slot's state as a message reported it. A slot mentioned at all is reported whole -
/// every protocol surveyed renders a slot as one atomic block - so these are plain values, not
/// <see cref="Field{T}"/> cells; a slot the message does not mention simply has no
/// <see cref="SlotUpdate"/>, and its stored row is untouched.
/// </summary>
/// <param name="SlotNumber">Which slot, in the printer's own numbering.</param>
/// <param name="Material">Loaded material, e.g. <c>PETG</c>.</param>
/// <param name="Temperature">The slot's nozzle temperature.</param>
/// <param name="HotendFanRpm">Hotend fan speed.</param>
/// <param name="PrintFanRpm">Print fan speed.</param>
public sealed record SlotUpdate(int SlotNumber,
                                string? Material,
                                float? Temperature,
                                float? HotendFanRpm,
                                float? PrintFanRpm);
