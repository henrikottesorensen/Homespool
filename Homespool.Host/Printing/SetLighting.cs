namespace Homespool.Host.Printing;

/// <summary>
/// Set how bright the printer's own lighting is - the LED strips inside a CORE One's chamber, or along
/// an XL's frame.
/// </summary>
/// <param name="Intensity">
/// Brightness in <b>percent</b>, 0 to <see cref="PrinterLighting.MaxIntensity"/>. Zero is off.
/// </param>
/// <remarks>
/// <b>The strip's ceiling, not a level it holds.</b> The printer dims the strip on its own when it
/// has been idle a while, and this sets how bright it is when it is not dimmed - which is what the
/// printer's own menu calls the same setting.
/// </remarks>
public sealed record SetLighting(int Intensity) : IPrinterIntent;
