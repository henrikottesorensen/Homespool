namespace Homespool.Model;

/// <summary>
/// An action whose failures are counted and backed off per account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each member names something one account can be ground at.</b> Most are a short secret an
/// authenticated caller could retry - a registration code on the claim page, an authenticator code
/// confirming a printer's removal or the disabling of two-factor. None of those sits behind the
/// anonymous global limiter, so without a per-account bound an account could try at request rate.
/// The two email members bound a <em>spend</em> rather than a guess: the anonymous forms that mail a
/// known address are counted per target account, because the address is the only stable handle an
/// anonymous caller offers and the cost lands on that account's inbox and the deployment's SMTP
/// quota either way.
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
    /// <b>Retired 2026-09-06, nothing counts it.</b> Confirming a printer's removal with an
    /// authenticator code goes through the <c>Totp</c> scheme now, whose wrong codes count toward
    /// the account lockout. The member stays because rows naming it may exist. (It was separate from
    /// <see cref="ClaimPrinter"/> so that fluffing a code would not back somebody off a claim they
    /// were standing at a printer to complete; a claim is still counted here, on its own.)
    /// </summary>
    RemovePrinter = 2,

    /// <summary>
    /// <b>Retired 2026-09-06, nothing counts it.</b> Confirming that two-factor is turned off went
    /// through the <c>Totp</c> scheme, as above. The member stays because rows naming it may exist.
    /// </summary>
    DisableTwoFactor = 3,

    /// <summary>
    /// A password-reset email sent by the anonymous forgot-password form, counted against the
    /// account it is addressed to.
    /// </summary>
    /// <remarks>
    /// The failure being counted is a send, not a wrong answer - each one costs the target an inbox
    /// entry and the deployment SMTP quota, and nothing else bounds an anonymous caller who knows an
    /// address. Completing the reset clears the count, so the backoff only ever stands between an
    /// address and mail nobody is acting on.
    /// </remarks>
    SendPasswordResetEmail = 4,

    /// <summary>
    /// A confirmation email sent by the anonymous resend-confirmation form, counted against the
    /// account it is addressed to.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SendPasswordResetEmail"/> for the same reason every member is
    /// separate: a flood of one kind of mail must not stop the other kind reaching its owner.
    /// </remarks>
    SendConfirmationEmail = 5,

    /// <summary>
    /// <b>Retired 2026-09-06, nothing counts it.</b> Proving the current password before a passkey may
    /// be added went through the <c>UserPassword</c> scheme, whose wrong passwords count toward the
    /// account lockout instead. The member stays because rows naming it may exist.
    /// </summary>
    AddPasskey = 6,
}
