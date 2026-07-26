using System.Collections.Generic;
using System.Threading.Tasks;

using PrinterService.Host.Services;

namespace PrinterService.Host.Test;

/// <summary>
/// An <see cref="IEmailSender"/> that records what it was asked to send instead of sending it, so a
/// test can assert on the recipient/subject/body without any real SMTP.
/// </summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    public List<(string email, string subject, string htmlMessage)> SentEmails { get; } = [];

    /// <summary>What <see cref="SendEmailAsync"/> reports back to the caller. <see cref="EmailSendResult.Sent"/> by default.</summary>
    public EmailSendResult Result { get; set; } = EmailSendResult.Sent;

    public Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage)
    {
        SentEmails.Add((email, subject, htmlMessage));

        return Task.FromResult(Result);
    }
}
