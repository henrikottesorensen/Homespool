namespace Homespool.Host.Middleware;

/// <summary>
/// Deployment-wide security choices that are an operator's to make, bound from the <c>Security</c>
/// configuration section.
/// </summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Whether every account must have an authenticator app configured. <b>Off by default</b>, which
    /// leaves two-factor authentication opt-in per account, as it has always been.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a requirement on the account, not on the session</b> (Henrik, 2026-08-22), which is
    /// what decides the awkward half: a personal access token belonging to an account with no
    /// authenticator stops working, rather than outliving the requirement that was turned on after it
    /// was minted. The cost is real and worth stating plainly — <b>turning this on breaks every
    /// existing integration whose owner has not enrolled</b>, and it breaks them as a 401 rather than
    /// as anything that explains itself. An operator turning it on should expect to hear about it.
    /// </para>
    /// <para>
    /// <b>What it does not reach: printers.</b> A machine has no second factor, so the printer schemes
    /// are outside this entirely — the interactive gate acts only on the application cookie, and the
    /// token check only on tokens.
    /// </para>
    /// <para>
    /// <b>The first administrator meets it immediately.</b> <c>Setup</c> creates an account with a
    /// password and no authenticator, so with this on, first run lands on the enrolment page before
    /// anything else. That is the intended reading of "all accounts" rather than an oversight.
    /// </para>
    /// </remarks>
    public bool RequireTwoFactor { get; set; }

    /// <summary>
    /// The hostname passkeys are bound to - the WebAuthn relying-party id. <b>Empty by default, which
    /// withholds passkeys</b>: there is no honest guess for a deployment that has not named itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It must be the name in the browser's address bar, or a parent of it.</b> A credential is
    /// minted against this value and answers only from a page served under it, so a household reaching
    /// the box by one name at home and another from outside gets passkeys on one of them. An IP
    /// address can never be a relying-party id, <c>localhost</c> is its own, and a name no public
    /// certificate authority issues for works only where the browser already trusts the certificate.
    /// </para>
    /// <para>
    /// <b>Changing it strands every passkey already enrolled</b>, which is why it is restart-graded
    /// and why the settings page asks before moving off a value in use. Nothing is deleted: putting
    /// the old name back brings the old credentials back with it.
    /// </para>
    /// <para>
    /// <b>What it is not: a rule about which hosts serve the application.</b> That is
    /// <c>AllowedHosts</c>. This names one of them as the one a passkey belongs to; a request on any
    /// other is served as before, with the passkey affordance withheld.
    /// </para>
    /// </remarks>
    public string? PasskeyServerDomain { get; set; }
}
