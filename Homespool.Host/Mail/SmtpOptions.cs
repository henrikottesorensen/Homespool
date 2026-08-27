using System;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Host.Mail;

/// <summary>
/// Outgoing mail configuration, bound from the <c>Smtp</c> configuration section.
/// </summary>
/// <remarks>
/// SMTP is optional. A self-hosted instance is not required to have a mail server, and the app stays fully usable
/// without one - see <see cref="IsConfigured"/> for what changes when it is absent.
/// </remarks>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>
    /// Mail server hostname. Empty (the default) means no SMTP, which is a supported configuration.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Mail server port. 587 (submission, STARTTLS) by default; 465 for implicit TLS, 25 for unencrypted.</summary>
    [Range(1, 65_535)]
    public int Port { get; set; } = 587;

    /// <summary>
    /// Connect with TLS already established rather than upgrading via STARTTLS. Set this for port 465.
    /// </summary>
    /// <remarks>
    /// This distinction is why the sender uses MailKit rather than <c>System.Net.Mail.SmtpClient</c>, which cannot
    /// do implicit TLS at all - its <c>EnableSsl</c> means STARTTLS on a plaintext port.
    /// </remarks>
    public bool UseImplicitTls { get; set; }

    /// <summary>
    /// Disables encryption entirely, connecting in the clear. <b>Never set this for anything reached
    /// over an untrusted network</b> - it exists for trusted local/dev SMTP relays that don't offer
    /// TLS at all (e.g. Mailpit's default configuration in a Docker container on the same host),
    /// where <see cref="UseImplicitTls"/>'s two encrypted modes have nothing to negotiate with.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate, explicit opt-in rather than a silent fallback when a TLS mode fails -
    /// that would reintroduce exactly the "credentials sent in the clear" risk documented on
    /// <see cref="SmtpEmailSender"/>'s <c>StartTls</c> choice. Default <c>false</c> keeps every
    /// existing deployment's behaviour unchanged.
    /// </remarks>
    public bool DisableTls { get; set; }

    /// <summary>Username for SMTP AUTH. Empty means connect without authenticating.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Password for SMTP AUTH.</summary>
    /// <remarks>
    /// Supply this through user-secrets, an environment variable or a Docker secret rather than appsettings.json,
    /// which is committed. The compose stack should pass it as <c>Smtp__Password</c>.
    /// </remarks>
    public string Password { get; set; } = string.Empty;

    /// <summary>Envelope sender address. Falls back to <see cref="UserName"/> when empty.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>Display name on outgoing mail.</summary>
    public string FromName { get; set; } = "Homespool";

    /// <summary>How long to wait on connect, authenticate and send before giving up, in seconds.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Connect and authenticate once at startup purely to report a broken configuration early. Diagnostic only.
    /// </summary>
    /// <remarks>
    /// Deliberately never changes behaviour - see <see cref="SmtpConnectivityProbe"/>. Disable it if the mail server
    /// lives in the same compose stack and routinely starts after this container, since the probe would then warn
    /// about a condition that resolves itself.
    /// </remarks>
    public bool ProbeOnStartup { get; set; } = true;

    /// <summary>
    /// Whether outgoing mail is configured at all. Everything conditional keys off this one property.
    /// </summary>
    /// <remarks>
    /// When false: <see cref="LoggingEmailSender"/> replaces the real sender, and accounts are created with
    /// <c>EmailConfirmed = true</c> because no confirmation mail can ever arrive. Note the *policy*
    /// (<c>SignIn.RequireConfirmedAccount</c>) stays true either way - the decision is made per account at creation,
    /// never by flipping the policy, so that configuring SMTP later cannot retroactively lock out existing accounts.
    /// </remarks>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    /// <summary>The address mail is sent from, resolving the <see cref="UserName"/> fallback.</summary>
    public string ResolvedFromAddress => string.IsNullOrWhiteSpace(FromAddress) ? UserName : FromAddress;

    /// <summary><see cref="TimeoutSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}
