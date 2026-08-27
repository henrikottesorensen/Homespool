namespace Homespool.Host.Mail;

/// <summary>
/// Creates an <see cref="ISmtpTransport"/> per send/probe attempt - a new one each time, mirroring
/// how <see cref="SmtpEmailSender"/>/<see cref="SmtpConnectivityProbe"/> already scoped a fresh
/// <c>SmtpClient</c> to each call before this seam existed.
/// </summary>
public interface ISmtpTransportFactory
{
    ISmtpTransport Create();
}

/// <summary>The production factory: a real MailKit-backed transport every time.</summary>
public sealed class MailKitSmtpTransportFactory : ISmtpTransportFactory
{
    public ISmtpTransport Create()
    {
        return new MailKitSmtpTransport();
    }
}
