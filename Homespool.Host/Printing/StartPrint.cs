using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Start printing a file already on the printer's own storage, named by the path the printer
/// knows it by. Getting the file there first is transfer machinery and deliberately not an
/// intent yet - it is per-protocol from end to end.
/// </summary>
public sealed record StartPrint(string Path) : IPrinterIntent
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Starting a print is the act queueing defers, so it is the same right - which is what lets
    /// <c>QueueAdvancer</c> ask for this as the person who queued the work, with no special case.
    /// </para>
    /// <para>
    /// <b>That is also why <c>QueueAdvancer</c> must stay its only sender.</b> This right is all a
    /// slicer's key holds, and firmware starts a print from <c>Finished</c> and <c>Stopped</c> as
    /// well as <c>Ready</c> - so a route sending this directly would print onto whatever the last
    /// print left, past the queue and the printer's remote-ready toggle alike. The queue is what
    /// waits for a person to ready the printer.
    /// </para>
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;
}
