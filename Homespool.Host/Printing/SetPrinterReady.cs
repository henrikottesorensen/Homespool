using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Mark the printer ready for the next job - a person's assertion that the bed is clear, never
/// inferred (<see cref="Model.PrinterStatus.Ready"/>). On Prusa Connect the printer owns this
/// state, so the intent has a wire command; a protocol without the concept has it owned by
/// Homespool instead, and the intent would not reach a wire at all.
/// </summary>
public sealed record SetPrinterReady : IPrinterIntent
{
    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>You cannot print without it</b>, which is the whole argument. The queue gates on
    /// <see cref="Model.PrinterStatus.Ready"/> and readying is per print - clearing a finished print at
    /// the panel leaves the printer idle - so under <see cref="Capability.ControlPrinter"/> somebody
    /// able to queue work could never start it.
    /// </para>
    /// <para>
    /// <b>Scoping this by ownership was considered and buys nothing.</b> The harm it would guard is a
    /// false assertion that the bed is clear, and neither edge constrains that: readying an empty queue
    /// then queueing reaches the same outcome in two steps, and readying to release somebody else's job
    /// is helpful when the bed is clear and harmful when it is not, exactly as for your own. Whoever
    /// just took their finished print off the sheet is in any case the best-informed assertor there is.
    /// </para>
    /// <para>
    /// <b>What protects the assertion is elsewhere and unchanged</b>: the per-printer
    /// <c>RemoteReadyAllowed</c> toggle, which is <see cref="Capability.ManagePrinter"/> and off by
    /// default, the checklist prompt, and the camera snapshot where there is one. None of it ever
    /// rested on which capability pressed the button.
    /// </para>
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;
}
