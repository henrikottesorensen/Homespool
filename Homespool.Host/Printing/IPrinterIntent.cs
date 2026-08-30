using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Something a caller wants a printer to do, in Homespool's own vocabulary - protocol-free by
/// construction. Each protocol translates an intent into its own wire command at the edge (for
/// Prusa Connect that is <c>PrusaIntentTranslator</c>), and a protocol that cannot express one
/// refuses it there rather than pretending. The vocabulary is drawn against more than one
/// protocol rather than against Prusa Connect alone.
/// </summary>
public interface IPrinterIntent
{
    /// <summary>
    /// The intent's own name, for logs and failure bodies - the type name, never a protocol's wire
    /// word: <c>StopPrint</c>, where the Prusa wire says <c>STOP_PRINT</c>.
    /// </summary>
    string Name => GetType().Name;

    /// <summary>
    /// What an account must hold to ask for this. <b>Declared by the intent, not by the caller</b>, so
    /// a new call site cannot pick a weaker one by accident.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than on the wire command</b>, because a permission is a domain question and this
    /// is the domain vocabulary: the same intent means the same thing whichever protocol spells it,
    /// so a second protocol inherits the answer instead of restating it.
    /// </para>
    /// <para>
    /// <b>The default is the strict answer</b>, so an intent that says nothing is treated as steering
    /// the machine. Overriding it is how an intent declares itself weaker, which is a decision worth
    /// making explicitly and in one visible place.
    /// </para>
    /// <para>
    /// <b><see cref="StopPrint"/> is the subtle one.</b> It declares <see cref="Capability.Print"/>,
    /// which alone would let anybody holding that stop anybody's print - so the ownership half is
    /// enforced above, by <c>PrintStopService</c>, which every path to a stop goes through. The
    /// capability here is the floor, not the whole rule.
    /// </para>
    /// </remarks>
    Capability RequiredCapability => Capability.ControlPrinter;

    /// <summary>
    /// Whether this intent may only be sent to a printer whose
    /// <see cref="Homespool.Model.Entities.Printer.RemoteReadyAllowed"/> is set. The default is
    /// false, so an intent is gated only if it says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared by the intent for the same reason <see cref="RequiredCapability"/> is</b>, and it
    /// buys the same thing: the check then happens once, where every caller already funnels through,
    /// and a new call site inherits it rather than restating it. It was restated until now - two page
    /// handlers read the flag and the API controller did not, which is exactly the shape this
    /// replaces.
    /// </para>
    /// <para>
    /// <b>Not a capability, because it is not a question about the caller.</b> A capability asks who
    /// may press the button; this asks whether this machine has been set up to be told remotely at
    /// all - a standing judgement its manager makes once, about a printer rather than about a person.
    /// The two are checked together and neither substitutes for the other.
    /// </para>
    /// </remarks>
    bool RequiresRemoteReadyAllowed => false;
}
