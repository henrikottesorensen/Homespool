namespace Homespool.Host.Printing;

/// <summary>
/// Set both heater targets, atomically - there is no single-heater intent, deliberately: the pair
/// exists because two separate commands race on protocols that queue gcode
/// (<c>Homespool.Host.PrusaConnect.Commands.SetTemperatures</c> carries the measured account), and
/// a caller wanting one heater passes the other's current target.
/// </summary>
/// <param name="NozzleTemperature">Nozzle target in <b>degrees Celsius</b>. Zero is off.</param>
/// <param name="BedTemperature">Bed target in <b>degrees Celsius</b>. Zero is off.</param>
public sealed record SetTemperatures(int NozzleTemperature, int BedTemperature) : IPrinterIntent;
