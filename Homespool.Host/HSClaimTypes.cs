namespace Homespool.Host;

public static class HSClaimTypes
{
    /// <summary>
    /// The printer's surrogate key. This is the internal <c>Printer.Id</c>, not the public
    /// <c>Uuid</c> — the principal never leaves the server, and dispatch needs the value that
    /// telemetry rows are keyed by.
    /// </summary>
    public const string PrinterId = "printer-id";

    public const string Owner = "owner";

    /// <summary>
    /// What a credential's scope permits, as <c>CapabilitySet</c> spells it. Written by the
    /// authentication handler for a credential that named a subset, and read by
    /// <see cref="Authorisation.CallerResolver"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Absent and empty are different, deliberately.</b> No claim means the credential named no
    /// subset - a sign-in cookie, or a token minted before scopes existed - and narrows nothing. A
    /// claim holding the empty string is a scope that grants nothing, which is a thing somebody can
    /// deliberately mint. Collapsing the two would make "restrict this token to nothing" unsayable.
    /// </para>
    /// <para>
    /// The scope travels as a claim rather than being re-read per request: it is settled at
    /// authentication, and a second lookup would only add a way for the two to disagree.
    /// </para>
    /// </remarks>
    public const string Scope = "scope";
}
