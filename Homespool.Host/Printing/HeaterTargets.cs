namespace Homespool.Host.Printing;

/// <summary>
/// What a printer's heaters were last set to, in whole degrees Celsius - 0 is off.
/// </summary>
/// <param name="Nozzle">The nozzle's target.</param>
/// <param name="Bed">The bed's target.</param>
public sealed record HeaterTargets(int Nozzle, int Bed);
