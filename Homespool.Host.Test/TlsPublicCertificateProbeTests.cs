using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Homespool.Host.Certificates;

namespace Homespool.Host.Test;

/// <summary>
/// The probe against a real TLS listener on loopback: what it reads from a certificate, and how it
/// tells a listener that refuses from one that is not there.
/// </summary>
/// <remarks>
/// A certificate a public authority issued cannot be produced here, so "trusted" is not tested; the
/// platform's chain building decides that, and these cases pin that its verdict is recorded rather
/// than acted on.
/// </remarks>
public sealed class TlsPublicCertificateProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static X509Certificate2 SelfSigned(string name, DateTimeOffset notAfter)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());

        return request.CreateSelfSigned(notAfter.AddDays(-30), notAfter);
    }

    /// <summary>A listener on a free loopback port, started.</summary>
    private static TcpListener Loopback()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int PortOf(TcpListener listener)
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Serves <paramref name="certificate"/> to one handshake, or closes the first connection unanswered.</summary>
    private static async Task ServeAsync(TcpListener listener, X509Certificate2? certificate,
                                         CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);

            if (certificate is null)
            {
                return;
            }

            await using SslStream tls = new(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate },
                                                cancellationToken);
        }
        catch (Exception e) when (e is System.IO.IOException or System.Security.Authentication.AuthenticationException)
        {
            // The client hanging up after the handshake, which is what it does.
        }
    }

    [Fact]
    public async Task A_self_signed_certificate_is_read_with_its_expiry_and_recorded_as_untrusted()
    {
        using CancellationTokenSource cancel = new(Timeout);
        DateTimeOffset notAfter = new(2027, 1, 2, 3, 4, 5, TimeSpan.Zero);
        using X509Certificate2 certificate = SelfSigned("public.example.com", notAfter);
        using TcpListener listener = Loopback();
        Task served = ServeAsync(listener, certificate, cancel.Token);

        PublicCertificateProbeResult result = await new TlsPublicCertificateProbe()
            .ProbeAsync("127.0.0.1", PortOf(listener), "public.example.com", cancel.Token);
        await served;

        result.Outcome.Should().Be(PublicCertificateProbeOutcome.Served);
        result.NotAfter.Should().Be(notAfter);
        result.Errors.Should().HaveFlag(SslPolicyErrors.RemoteCertificateChainErrors);
        result.Errors.Should().NotHaveFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_recorded_as_a_name_mismatch()
    {
        using CancellationTokenSource cancel = new(Timeout);
        using X509Certificate2 certificate = SelfSigned("other.example.com", DateTimeOffset.UtcNow.AddDays(60));
        using TcpListener listener = Loopback();
        Task served = ServeAsync(listener, certificate, cancel.Token);

        PublicCertificateProbeResult result = await new TlsPublicCertificateProbe()
            .ProbeAsync("127.0.0.1", PortOf(listener), "public.example.com", cancel.Token);
        await served;

        result.Errors.Should().HaveFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
    }

    [Fact]
    public async Task A_listener_that_hangs_up_is_a_refusal_not_an_absence()
    {
        using CancellationTokenSource cancel = new(Timeout);
        using TcpListener listener = Loopback();
        Task served = ServeAsync(listener, null, cancel.Token);

        PublicCertificateProbeResult result = await new TlsPublicCertificateProbe()
            .ProbeAsync("127.0.0.1", PortOf(listener), "public.example.com", cancel.Token);
        await served;

        result.Outcome.Should().Be(PublicCertificateProbeOutcome.Refused);
        result.Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Nothing_listening_is_unreachable()
    {
        using CancellationTokenSource cancel = new(Timeout);
        int port;

        using (TcpListener listener = Loopback())
        {
            port = PortOf(listener);
        }

        PublicCertificateProbeResult result = await new TlsPublicCertificateProbe()
            .ProbeAsync("127.0.0.1", port, "public.example.com", cancel.Token);

        result.Outcome.Should().Be(PublicCertificateProbeOutcome.Unreachable);
    }
}
