using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace PrinterService.Host.Services;

/// <summary>
/// Fallback <see cref="IEmailSender"/> for deployments with no SMTP configured: logs the message instead of sending it.
/// </summary>
/// <remarks>
/// A self-hosted instance is not required to have a mail server, so this keeps the page models that inject
/// <see cref="IEmailSender"/> working rather than failing at resolution time. The message body is logged at Debug
/// because confirmation and reset links are credentials - they should not appear in default-level logs.
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
        _logger.LogDebug("Unsent email body for {Email}: {HtmlMessage}", email, htmlMessage);

        // Not a failure: without SMTP, accounts are created already confirmed and nobody is waiting on this message.
        return Task.FromResult(EmailSendResult.NotConfigured);
    }
}
