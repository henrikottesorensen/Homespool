using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Homespool.Host.Services;

/// <summary>
/// The real <see cref="ISmtpTransport"/>, delegating every call straight to a MailKit
/// <see cref="SmtpClient"/>. See <see cref="ISmtpTransport"/> for why this seam exists.
/// </summary>
public sealed class MailKitSmtpTransport : ISmtpTransport
{
    private readonly SmtpClient _client = new();

    /// <summary>Uses .NET's default certificate chain validation, unchanged - the production path.</summary>
    public MailKitSmtpTransport()
    {
    }

    /// <summary>
    /// Overrides certificate validation. Not used by <see cref="MailKitSmtpTransportFactory"/> -
    /// exists solely for <c>Homespool.Host.IntegrationTest</c> to pin the self-signed
    /// certificate <c>start-mailpit-tls.sh</c> generates, since a self-signed certificate fails
    /// default chain validation. Deliberately a second constructor rather than an optional
    /// parameter defaulting to <c>null</c>, so the production call site
    /// (<c>new MailKitSmtpTransport()</c>) can never accidentally end up passing one.
    /// </summary>
    public MailKitSmtpTransport(RemoteCertificateValidationCallback serverCertificateValidationCallback)
    {
        _client.ServerCertificateValidationCallback = serverCertificateValidationCallback;
    }

    public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken)
    {
        return _client.ConnectAsync(host, port, options, cancellationToken);
    }

    public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        return _client.AuthenticateAsync(userName, password, cancellationToken);
    }

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        return _client.SendAsync(message, cancellationToken);
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
    {
        return _client.DisconnectAsync(quit, cancellationToken);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
