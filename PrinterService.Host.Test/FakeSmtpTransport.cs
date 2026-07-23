using System;
using System.Threading;
using System.Threading.Tasks;

using MailKit.Security;

using MimeKit;

using PrinterService.Host.Services;

namespace PrinterService.Host.Test;

/// <summary>
/// Hand-rolled <see cref="ISmtpTransport"/> double, matching this project's existing style of
/// writing fakes directly (<c>FakeWebSocketPipe</c>) rather than pulling in a mocking framework.
/// Records what was called with which arguments, and can be told to throw at any stage to exercise
/// <see cref="SmtpEmailSender"/>/<see cref="SmtpConnectivityProbe"/>'s failure paths.
/// </summary>
public sealed class FakeSmtpTransport : ISmtpTransport
{
    public (string Host, int Port, SecureSocketOptions Options)? ConnectCall { get; private set; }

    public (string UserName, string Password)? AuthenticateCall { get; private set; }

    public MimeMessage? SentMessage { get; private set; }

    /// <summary>
    /// The HTML body, captured here rather than read back off <see cref="SentMessage"/> later:
    /// <c>SmtpEmailSender</c> disposes its <c>MimeMessage</c> in a <c>using</c> before returning, so
    /// <see cref="MimeMessage.HtmlBody"/> throws <see cref="ObjectDisposedException"/> by the time a
    /// test gets to assert on it.
    /// </summary>
    public string? SentHtmlBody { get; private set; }

    public bool? DisconnectedWithQuit { get; private set; }

    public Exception? ThrowOnConnect { get; set; }

    public Exception? ThrowOnAuthenticate { get; set; }

    public Exception? ThrowOnSend { get; set; }

    public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken)
    {
        ConnectCall = (host, port, options);

        return ThrowOnConnect is null ? Task.CompletedTask : Task.FromException(ThrowOnConnect);
    }

    public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        AuthenticateCall = (userName, password);

        return ThrowOnAuthenticate is null ? Task.CompletedTask : Task.FromException(ThrowOnAuthenticate);
    }

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        SentMessage = message;
        SentHtmlBody = message.HtmlBody;

        return ThrowOnSend is null ? Task.CompletedTask : Task.FromException(ThrowOnSend);
    }

    public Task DisconnectAsync(bool quit, CancellationToken cancellationToken)
    {
        DisconnectedWithQuit = quit;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

/// <summary>Hands back the single <see cref="FakeSmtpTransport"/> given at construction, every time.</summary>
public sealed class FakeSmtpTransportFactory : ISmtpTransportFactory
{
    private readonly FakeSmtpTransport _transport;

    public FakeSmtpTransportFactory(FakeSmtpTransport transport)
    {
        _transport = transport;
    }

    public ISmtpTransport Create() => _transport;
}
