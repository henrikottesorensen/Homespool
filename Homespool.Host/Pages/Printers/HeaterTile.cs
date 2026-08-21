namespace Homespool.Host.Pages.Printers;

/// <summary>
/// One heater's tile on the status card - the label it goes under, and what to say.
/// </summary>
/// <remarks>
/// A model for <c>_HeaterTile.cshtml</c>, which the nozzle and the bed both render. They differ only
/// in the word above them, and two copies of the same markup would be two places to fix the day the
/// tile changes.
/// </remarks>
/// <param name="Label">Already localised, because the caller knows which heater this is.</param>
/// <param name="Reading">Where the heater is, and what that means.</param>
public sealed record HeaterTile(string Label, HeaterReading Reading);
