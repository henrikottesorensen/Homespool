namespace Homespool.Model;

/// <summary>
/// One thing an account may do — the whole permission vocabulary, for printers and cameras alike.
/// A <c>TeamMember</c> carries a set of these, and a personal access token will eventually carry a
/// subset of its owner's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here in <c>Homespool.Model</c> because it is persisted.</b> <c>TeamMember.Capabilities</c> is
/// typed in it, and persistence is what decides where a vocabulary lives: pass-through vocabulary
/// stays at the edge, stored vocabulary lands in the domain project. These names were
/// <c>PrinterOperation</c> and <c>CameraOperation</c> under <c>Homespool.Host/Authorisation/</c>
/// precisely while nothing stored them.
/// </para>
/// <para>
/// <b>One enum rather than one per resource</b>, because the stored form is a single space-separated
/// namespace. Two enums would buy a compile-time separation that serialisation discards on the next
/// line, and would need a collision rule between them for nothing. The cost is real and worth
/// knowing: a printer gate can be handed <see cref="ManageCamera"/> and will compile. It grants
/// nothing — a capability only permits what a gate maps it to — but it is nonsense that no longer
/// fails to build, which is why the two access services stay separate.
/// </para>
/// <para>
/// <b>Renaming a member here is a data migration</b>, not a rename refactor: the name is what sits in
/// the column. That is the accepted cost of a readable row, the same trade already made for
/// <c>PrintHoldReason</c>.
/// </para>
/// </remarks>
public enum Capability
{
    /// <summary>
    /// Nobody said which capability. <b>Never granted, and never written</b> — parsing rejects the
    /// name and the gates refuse it.
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
    ViewPrinter = 1,

    /// <summary>See what a printer is going to print, and why the queue is waiting.</summary>
    ViewQueue = 2,

    /// <summary>See what a printer has printed.</summary>
    ViewHistory = 3,

    /// <summary>See a camera and its picture.</summary>
    ViewCamera = 4,

    /// <summary>
    /// Queue a print — <b>and everything the queue loop does on your behalf to make it happen</b>,
    /// which is the transfer and <c>START_PRINT</c>. Also withdrawing that work: cancelling your own
    /// queue entry, and stopping your own running print.
    /// </summary>
    /// <remarks>
    /// <b>Queueing is printing, deferred</b> — putting a file in the queue is causing that printer to
    /// print, and the only difference is latency. Naming the right for the outcome rather than the
    /// mechanism is what lets <c>QueueAdvancer</c> act as the person who queued the work without a
    /// special case: the queuer holds this, the loop sends as the queuer, and the send needs this.
    /// Splitting "queue" from "start" instead would let somebody queue a print that could never run.
    /// <para>
    /// <b>It covers your own work only.</b> Stopping or cancelling somebody else's is
    /// <see cref="ControlPrinter"/>. That is the lpd/CUPS split this project already claims — a user
    /// withdraws their own job, an operator withdraws any — and it exists so the person who caused a
    /// print is not the one person unable to stop it.
    /// </para>
    /// </remarks>
    Print = 5,

    /// <summary>
    /// Steer the machine, whoever is using it: stop, pause, resume, ready, unready, idle and preheat,
    /// on anyone's print, and cancel anyone's queue entry.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Print"/> because running a printer is not the same act as putting
    /// work on it. Note the pairing with <see cref="ManagePrinter"/> that already exists: pressing
    /// <i>Set ready</i> is this, while allowing the button to be pressed remotely at all is a policy
    /// decision and belongs to the manager.
    /// </remarks>
    ControlPrinter = 6,

    /// <summary>
    /// Change the printer itself: its name, its location, its enrolment, and the remote-ready policy.
    /// </summary>
    ManagePrinter = 7,

    /// <summary>Add, change or remove a camera.</summary>
    /// <remarks>
    /// There is deliberately no <c>UseCamera</c> between viewing and managing: a camera's address is
    /// fetched by the thing that shows it, so adding one is where the judgement is.
    /// </remarks>
    ManageCamera = 8,

    /// <summary>List and download <b>your own</b> files.</summary>
    /// <remarks>
    /// <para>
    /// <b>The three file capabilities are about a credential, not about access between people.</b>
    /// A file lives at <c>{userId}/{name}</c>, so no membership can grant somebody else's and none
    /// can withhold your own - which makes these near-vacuous as a grant and load-bearing as a token
    /// scope. <c>Own</c> is in the name so a row saying <c>ViewOwnFiles</c> reads as what it is rather
    /// than as a claim over somebody's library.
    /// </para>
    /// <para>
    /// <b>They are therefore not in <c>CapabilityPresets</c></b>, which are membership presets. There
    /// is nothing for a membership to say about them.
    /// </para>
    /// </remarks>
    ViewOwnFiles = 9,

    /// <summary>Upload a file under a name you are not already using.</summary>
    /// <remarks>
    /// Separate from <see cref="ManipulateOwnFiles"/> on a concrete driver: PrusaSlicer's print-host
    /// key needs upload and print and nothing else, and under a view/manage split it would have needed
    /// the destructive one - so a key sitting in a slicer's config could erase every model its owner
    /// has.
    /// </remarks>
    UploadOwnFiles = 10,

    /// <summary>Rename, delete, and overwrite your own files.</summary>
    /// <remarks>
    /// <b>Overwrite belongs here rather than with <see cref="UploadOwnFiles"/></b>: replacing the bytes
    /// under a name that already exists is manipulation whatever the verb says, and
    /// <i>upload own files</i> should not sound like it destroys one. Without this an overwriting
    /// upload is refused, which is the 409 an existing name already gives.
    /// </remarks>
    ManipulateOwnFiles = 11,
}
