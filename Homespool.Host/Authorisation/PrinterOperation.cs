namespace Homespool.Host.Authorisation;

/// <summary>
/// The things a person can ask to do to a printer, named by the <i>operation</i> rather than by the
/// permission it currently needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Operations rather than <c>TeamMember</c>'s three flags</b> (Henrik, 2026-08-03). Six names over
/// three booleans looks like more vocabulary than the model has, and that is the point: the mapping
/// from operation to flag lives in exactly one place -
/// <see cref="PrinterAccessService.RequiredPermission"/> - so the day viewing history needs a
/// permission of its own, one line changes rather than a search for every site that happened to
/// spell it <c>CanRead</c>.
/// </para>
/// <para>
/// Read a call site as what the caller is doing, not as which column it consults. The column is an
/// implementation detail of the answer.
/// </para>
/// <para>
/// <b>Printer-scoped only.</b> Permissions that are about a team rather than a printer - claiming one
/// into a team, editing membership - are not here and are checked against the team directly, because
/// there is no printer for this to resolve.
/// </para>
/// </remarks>
public enum PrinterOperation
{
    /// <summary>
    /// Nobody said which operation. <b>Never granted</b> -
    /// <see cref="PrinterAccessService.RequiredPermission"/> throws on it.
    /// </summary>
    /// <remarks>
    /// <b>Here so that <c>default</c> refuses rather than grants.</b> Without it the zero value would
    /// be <see cref="ViewPrinter"/>, so a field nobody initialised, a deserialised zero or a forgotten
    /// argument would quietly ask for the most permissive read instead of failing. A permission enum
    /// is the one place a default has to mean "nothing".
    /// <para>
    /// <b>CA1008 does not catch this</b>, which is worth knowing before trusting the analyser here:
    /// the rule wants <i>a</i> zero-valued member, and <c>ViewPrinter</c> being zero satisfied it
    /// perfectly while failing open.
    /// </para>
    /// </remarks>
    Undefined = 0,

    /// <summary>See that the printer exists at all, and its name, state and telemetry.</summary>
    ViewPrinter,

    /// <summary>See what a printer is going to print, and why the queue is waiting.</summary>
    ViewQueue,

    /// <summary>See what a printer has printed.</summary>
    ViewHistory,

    /// <summary>Add to, reorder or cancel entries in the queue.</summary>
    ChangeQueue,

    /// <summary>Send the hardware a command - pause, resume, stop, ready, print.</summary>
    ControlPrinter,

    /// <summary>Change the printer itself: its name, its location, its enrolment.</summary>
    ManagePrinter,
}
