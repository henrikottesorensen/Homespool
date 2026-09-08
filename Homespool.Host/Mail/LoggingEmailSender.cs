using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.Mail;

/// <summary>
/// Fallback <see cref="IEmailSender"/> for deployments with no SMTP configured: logs the message instead of sending it.
/// </summary>
/// <remarks>
/// <para>
/// A self-hosted instance is not required to have a mail server, so this keeps the page models that inject
/// <see cref="IEmailSender"/> working rather than failing at resolution time.
/// </para>
/// <para>
/// The recipient and subject are recorded; the body is not, at any level. These messages carry password-reset,
/// address-confirmation and invitation links, and such a link is a credential - it is the whole of what the
/// recipient has to prove who they are. A log is the artifact that gets shipped to an aggregator and pasted into
/// a bug report, and lowering the level to diagnose a sign-in problem must not start collecting live reset tokens
/// as a side effect. An administrator creating an invitation is shown that link on the page that creates it.
/// </para>
/// </remarks>
public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        _logger = logger;
    }

    public Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage)
    {
        _logger.LogInformation("No SMTP configured; email to {Email} with subject {Subject} was not sent.", email, subject);

        // Not a failure: without SMTP, accounts are created already confirmed and nobody is waiting on this message.
        return Task.FromResult(EmailSendResult.NotConfigured);
    }
}
