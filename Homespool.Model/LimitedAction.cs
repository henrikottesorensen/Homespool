namespace Homespool.Model;

/// <summary>
/// An action whose failures are counted and backed off per account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each member names a guess somebody could grind at.</b> The two here are the ones where a
/// short secret is checked against something an authenticated caller can retry: a registration code
/// on the claim page, and an authenticator code confirming a printer's removal. Neither sits behind
/// the anonymous global limiter, so without a per-account bound an account could try at request
/// rate.
/// </para>
/// <para>
/// <b>Members are pinned and zero is reserved.</b> This one is persisted as text in
/// <c>UserActionAttempt.Action</c>,
/// so a reordering must not be able to relabel an existing row - and <see cref="Undefined"/> keeps
/// <c>default</c> from silently naming a real action.
/// </para>
/// </remarks>
public enum LimitedAction
{
    /// <summary>
    /// Nobody said which action. <b>Never counted and never stored</b> - a limiter asked about this
    /// is being asked a question nobody meant to ask.
    /// </summary>
    Undefined = 0,

    /// <summary>Claiming a printer with a registration code, on <c>Pages/Printers/Claim</c>.</summary>
    ClaimPrinter = 1,

    /// <summary>
    /// Confirming a printer's removal with an authenticator code, on the printer detail page.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ClaimPrinter"/> deliberately, which is the whole reason this enum
    /// exists rather than one shared counter: fluffing an authenticator code must not back somebody
    /// off a claim they are standing at a printer to complete, and neither should the reverse.
    /// </remarks>
    RemovePrinter = 2,
}
