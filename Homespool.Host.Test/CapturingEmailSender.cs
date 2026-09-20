using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.Accounts;
using Homespool.Host.Mail;

namespace Homespool.Host.Test;

/// <summary>
/// An <see cref="IEmailSender"/> that records what it was asked to send instead of sending it, so a
/// test can assert on the recipient/subject/body without any real SMTP.
/// </summary>
/// <remarks>
/// <b>It is also the <see cref="IDeferredEmailSender"/></b>, recording into the same list, so that a
/// test of a page that queues its mail asserts on what the page handed over. Nothing here drains a
/// queue: that this type captures both ways is what keeps the assertion about the page's decision
/// rather than about the background loop, which <c>DeferredEmailSenderTests</c> covers on its own.
/// </remarks>
internal sealed class CapturingEmailSender : IEmailSender, IDeferredEmailSender
{
    public List<(string email, string subject, string htmlMessage)> SentEmails { get; } = [];

    /// <summary>What <see cref="SendEmailAsync"/> reports back to the caller. <see cref="EmailSendResult.Sent"/> by default.</summary>
    public EmailSendResult Result { get; set; } = EmailSendResult.Sent;

    public Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage)
    {
        SentEmails.Add((email, subject, htmlMessage));

        return Task.FromResult(Result);
    }

    public void Enqueue(string email, string subject, string htmlMessage)
    {
        SentEmails.Add((email, subject, htmlMessage));
    }

    /// <summary>A <see cref="CredentialNotices"/> that mails through this sender.</summary>
    public CredentialNotices Notices()
    {
        return new CredentialNotices(this, TestLocaliser.Shared(), NullLogger<CredentialNotices>.Instance);
    }
}
