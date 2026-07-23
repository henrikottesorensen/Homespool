using System;
using System.Threading;
using System.Threading.Tasks;

using MailKit.Security;

using MimeKit;

namespace PrinterService.Host.Services;

/// <summary>
/// The handful of <c>MailKit.Net.Smtp.SmtpClient</c> operations <see cref="SmtpEmailSender"/> and
/// <see cref="SmtpConnectivityProbe"/> actually use, behind a seam so tests can substitute a fake
/// transport instead of talking to a real (or fake) SMTP server.
/// </summary>
/// <remarks>
/// The point is not to doubt MailKit's own correctness - its wire-protocol handling is trusted as
/// given - only to make <em>our</em> code (socket-option selection, conditional auth, envelope
/// construction, result mapping) verifiable without a network. <see cref="MailKitSmtpTransport"/> is
/// the only production implementation, and it does nothing but delegate to a real
/// <c>SmtpClient</c> 1:1.
/// </remarks>
public interface ISmtpTransport : IDisposable
{
    Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken);

    Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken);

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);

    Task DisconnectAsync(bool quit, CancellationToken cancellationToken);
}
