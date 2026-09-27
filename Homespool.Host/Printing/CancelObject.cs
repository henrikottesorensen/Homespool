using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Stop printing one object of the running print and carry on with the rest.
/// </summary>
/// <param name="ObjectId">
/// The object's id, <b>0-based</b> - the slicer's own index, as the printer reports it. Never a number
/// a person read off a screen, which counts from one.
/// </param>
public sealed record CancelObject(int ObjectId) : IPrinterIntent
{
    /// <inheritdoc />
    /// <remarks>
    /// <b>The floor only.</b> Cancelling an object is withdrawing part of somebody's work, and only
    /// the person who queued the print may do it - <see cref="PrintObjectService"/> decides that, and
    /// every cancel goes through it. Unlike a stop, <see cref="Capability.ControlPrinter"/> does not
    /// widen it to anybody's print.
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;
}
