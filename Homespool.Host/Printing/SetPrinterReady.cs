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
    /// <b>What protects the assertion is the per-printer <c>RemoteReadyAllowed</c> toggle</b>, which
    /// is <see cref="Capability.ManagePrinter"/> and off by default, the checklist prompt, and the
    /// camera snapshot where there is one. None of it ever rested on which capability pressed the
    /// button.
    /// </para>
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>This is the intent the toggle was always about</b>, so it is the one that declares itself
    /// gated by it. Until now the flag was a policy its two page handlers applied on the way past,
    /// and <c>PUT /api/v1/printers/{uuid}/command/ready</c> reached the same wire command without
    /// consulting it - deliberately, on the argument that writing a script is already the deliberate
    /// act the walk to the printer stood in for.
    /// </para>
    /// <para>
    /// <b>That argument was reconsidered and the decision reversed</b> (2026-08-30): it holds for the
    /// owner scripting their own machine, and holds less well for a member who was handed
    /// <see cref="Capability.Print"/> and a personal access token. Declaring it here makes the toggle
    /// mean what its name says on every route at once, rather than on the routes somebody remembered.
    /// </para>
    /// </remarks>
    public bool RequiresRemoteReadyAllowed => true;
}
