using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Homespool.Host.Mail;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// An <see cref="ISmtpTransportFactory"/> that validates the server's certificate chain against a
/// caller-supplied trust anchor - the throwaway CA <c>generate-test-ca.sh</c> creates - rather than
/// the OS trust store, and rather than pinning one specific leaf certificate.
/// </summary>
/// <remarks>
/// <para>
/// Builds its own <see cref="X509Chain"/> with <see cref="X509ChainTrustMode.CustomRootTrust"/> and
/// exactly one entry in <see cref="X509ChainPolicy.CustomTrustStore"/> - the OS trust store is never
/// consulted, so this never depends on (or risks polluting) machine-level trust. Real chain
/// validation runs against that anchor: certificate dates, signature, and the chain actually leading
/// to the trusted CA are all checked, unlike a bare thumbprint comparison.
/// </para>
/// <para>
/// Revocation checking is off (<see cref="X509RevocationMode.NoCheck"/>) - the throwaway CA
/// publishes no CRL or OCSP responder, so checking would only ever fail or hang.
/// </para>
/// </remarks>
public sealed class CustomCaSmtpTransportFactory : ISmtpTransportFactory
{
    private readonly X509Certificate2 _trustedCa;

    public CustomCaSmtpTransportFactory(X509Certificate2 trustedCa)
    {
        _trustedCa = trustedCa;
    }

    public ISmtpTransport Create()
    {
        return new MailKitSmtpTransport(ValidateAgainstTrustedCa);
    }

    private bool ValidateAgainstTrustedCa(object sender,
                                          X509Certificate? certificate,
                                          X509Chain? chain,
                                          SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null)
        {
            return false;
        }

        using X509Certificate2 presented = new(certificate);
        using X509Chain validationChain = new();

        validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        validationChain.ChainPolicy.CustomTrustStore.Add(_trustedCa);
        validationChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return validationChain.Build(presented);
    }
}
