using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Certificates;

/// <summary>
/// A TLS handshake and nothing after it: the certificate the listener serves for a name, and what the
/// platform's validation says of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validated as a browser would, and accepted regardless.</b> The callback records the errors and
/// answers yes, because the point is to read a certificate that is wrong, not to refuse it. The errors
/// come from the same chain building every other outbound TLS call here relies on, against the image's
/// trust store - Web Push reaches its push services through it - so "trusted" means trusted by the
/// roots browsers ship, not by anything this deployment added.
/// </para>
/// <para>
/// No request is sent. A handshake is all the question needs, and the proxy then sees a connection
/// closed after the handshake rather than a request it would log.
/// </para>
/// </remarks>
public sealed class TlsPublicCertificateProbe : IPublicCertificateProbe
{
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation",
                     Justification = "Reads the certificate rather than trusting it: the validation errors are recorded and judged, and nothing is sent over the connection.")]
    public async Task<PublicCertificateProbeResult> ProbeAsync(string host, int port, string name,
                                                               CancellationToken cancellationToken)
    {
        using TcpClient tcp = new();

        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken);
        }
        catch (SocketException e)
        {
            return PublicCertificateProbeResult.Unreachable(e.Message);
        }

        DateTimeOffset? notAfter = null;
        SslPolicyErrors errors = SslPolicyErrors.None;

        await using SslStream tls = new(tcp.GetStream(), leaveInnerStreamOpen: false);

        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = name,
                RemoteCertificateValidationCallback = (_, certificate, _, policyErrors) =>
                {
                    if (certificate is X509Certificate2 served)
                    {
                        notAfter = new DateTimeOffset(served.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                    }

                    errors = policyErrors;
                    return true;
                },
            }, cancellationToken);
        }
        catch (AuthenticationException e)
        {
            return PublicCertificateProbeResult.Refused(e.Message);
        }
        catch (IOException e)
        {
            return PublicCertificateProbeResult.Refused(e.Message);
        }

        return notAfter is DateTimeOffset expiry ?
            PublicCertificateProbeResult.Served(expiry, errors) :
            PublicCertificateProbeResult.Refused("the handshake completed with no certificate");
    }
}
