namespace Homespool.Model;

/// <summary>
/// An action bounded per account - its failures counted and backed off, or each use followed by a
/// cooldown.
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
/// quota either way. The two members sent from the signed-in address page are not counted at all:
/// each send starts a fixed cooldown, since the caller is known and a flat wait bounds the rate
/// without ever holding the owner off for longer than that.
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
    /// <b>Retired 2026-09-06, superseded by <see cref="StepUp"/>.</b> Confirming a printer's removal
    /// with an authenticator code goes through the <c>Totp</c> scheme now, and every step-up shares
    /// one counter. The member stays because rows naming it may exist. (It was separate from
    /// <see cref="ClaimPrinter"/> so that fluffing a code would not back somebody off a claim they
    /// were standing at a printer to complete; a claim is still counted on its own.)
    /// </summary>
    RemovePrinter = 2,

    /// <summary>
    /// <b>Retired 2026-09-06, superseded by <see cref="StepUp"/>.</b> Confirming that two-factor is
    /// turned off goes through the <c>Totp</c> scheme now. The member stays because rows naming it may
    /// exist.
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
    /// <b>Retired 2026-09-06, superseded by <see cref="StepUp"/>.</b> Proving the current password
    /// before a passkey may be added goes through the <c>UserPassword</c> scheme now. The member stays
    /// because rows naming it may exist.
    /// </summary>
    AddPasskey = 6,

    /// <summary>
    /// A step-up on the signed-in account: the password or the authenticator code typed again, inside
    /// a session, before an act the session alone may not do - adding a passkey, removing a printer,
    /// turning two-factor off, enrolling an authenticator. One counter for all of them, kept by the
    /// credential schemes themselves.
    /// </summary>
    /// <remarks>
    /// <b>Backed off here rather than counted toward the account lockout, deliberately.</b> A step-up
    /// exists to guard against somebody holding a session - an unlocked browser, a stolen cookie. Under
    /// the account lockout, five wrong codes typed by that somebody would lock the owner out of signing
    /// in, which is exactly how the owner regains control; every five minutes, for as long as they
    /// cared to. This backoff slows the guessing the same way and touches nothing but the step-up. The
    /// login page's own counting is a different matter: reaching a code there already takes the
    /// password.
    /// </remarks>
    StepUp = 7,

    /// <summary>
    /// A change-of-address link sent from <c>Pages/Account/Manage/Email</c>, to the address typed into
    /// it. Held to a fixed cooldown after each send rather than counted.
    /// </summary>
    /// <remarks>
    /// The recipient is whoever the signed-in account names, so this is the one form here that can
    /// point the deployment's mail at a third party. The recent proof decides who may send; this
    /// decides how often.
    /// </remarks>
    ChangeEmail = 8,

    /// <summary>
    /// A verification email the signed-in account sends to its own address, from
    /// <c>Pages/Account/Manage/Email</c>. Held to a fixed cooldown after each send rather than counted.
    /// </summary>
    /// <remarks>
    /// Not <see cref="SendConfirmationEmail"/>, though it is the same mail: that counter is spent by
    /// anonymous callers who know the address, and a short cooldown written into its row could cut a
    /// longer backoff short. Separate from <see cref="ChangeEmail"/> so that verifying and then
    /// correcting an address does not mean waiting between the two.
    /// </remarks>
    SendVerificationEmail = 9,
}
