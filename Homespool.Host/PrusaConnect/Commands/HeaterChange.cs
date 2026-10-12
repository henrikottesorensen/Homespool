namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// The heater targets a command leaves behind, in whole degrees Celsius - see
/// <see cref="ISendableCommand.LeavesHeaters"/>.
/// </summary>
/// <param name="Nozzle">The nozzle's target afterwards, or null when the command does not set it.</param>
/// <param name="Bed">The bed's target afterwards, or null when the command does not set it.</param>
public sealed record HeaterChange(int? Nozzle, int? Bed);
