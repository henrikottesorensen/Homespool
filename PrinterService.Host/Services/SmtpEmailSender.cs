using System.Threading;
using System.Threading.Tasks;

using MailKit.Security;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MimeKit;
using MimeKit.Text;

namespace PrinterService.Host.Services;

/// <summary>
/// Sends mail through the SMTP server configured in <see cref="SmtpOptions"/>.
/// </summary>
/// <remarks>
/// Only registered when <see cref="SmtpOptions.IsConfigured"/> is true; otherwise
/// <see cref="LoggingEmailSender"/> takes its place.
/// </remarks>
public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ISmtpTransportFactory _transportFactory;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ISmtpTransportFactory transportFactory, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _transportFactory = transportFactory;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendEmailAsync(string email, string subject, string htmlMessage)
    {
        using MimeMessage message = new();
        message.From.Add(new MailboxAddress(_options.FromName, _options.ResolvedFromAddress));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = subject;
        message.Body = new TextPart(TextFormat.Html) { Text = htmlMessage };

        using CancellationTokenSource timeout = new(_options.Timeout);
        using ISmtpTransport client = _transportFactory.Create();

        try
        {
            // SecureSocketOptions.StartTls *requires* the upgrade and fails if the server does not offer it, which is
            // what we want on 587: silently falling back to plaintext would send credentials in the clear.
            SecureSocketOptions socketOptions = _options.DisableTls
                ? SecureSocketOptions.None
                : _options.UseImplicitTls
                    ? SecureSocketOptions.SslOnConnect
                    : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_options.Host, _options.Port, socketOptions, timeout.Token);

            if (!string.IsNullOrWhiteSpace(_options.UserName))
            {
                await client.AuthenticateAsync(_options.UserName, _options.Password, timeout.Token);
            }

            await client.SendAsync(message, timeout.Token);
            await client.DisconnectAsync(true, timeout.Token);

            _logger.LogInformation("Sent email to {Email} with subject {Subject}.", email, subject);

            return EmailSendResult.Sent;
        }
        catch (System.Exception ex)
        {
            // Reported, not thrown: these callers are page handlers, and several have already created a user row by
            // the time mail is sent. Each call site decides whether to surface this - most should, but password reset
            // and confirmation resend must not, because there the send is only attempted when the account exists.
            _logger.LogError(ex, "Failed to send email to {Email} with subject {Subject}.", email, subject);

            return EmailSendResult.Failed;
        }
    }
}
