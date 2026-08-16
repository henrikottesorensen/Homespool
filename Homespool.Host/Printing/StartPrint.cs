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
    /// Starting a print is the act queueing defers, so it is the same right - which is what lets
    /// <c>QueueAdvancer</c> ask for this as the person who queued the work, with no special case.
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;
}
