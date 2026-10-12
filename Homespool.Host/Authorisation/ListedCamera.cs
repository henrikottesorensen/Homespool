using Homespool.Model.Entities;

namespace Homespool.Host.Authorisation;

/// <summary>
/// A camera as a listing shows it to one caller: with the printer it watches only when they may
/// see that printer.
/// </summary>
/// <param name="Camera">
/// The camera, with <see cref="Camera.Printer"/> deliberately not loaded - <paramref name="Printer"/>
/// is the only way a listing names it.
/// </param>
/// <param name="Printer">
/// The bound printer, or null when there is none or when the caller does not hold
/// <c>ViewPrinter</c> on it.
/// </param>
public sealed record ListedCamera(Camera Camera, Printer? Printer)
{
    /// <summary>Whether the camera is bound to a printer this caller may not see.</summary>
    public bool PrinterHidden => Camera.PrinterId is not null && Printer is null;
}
