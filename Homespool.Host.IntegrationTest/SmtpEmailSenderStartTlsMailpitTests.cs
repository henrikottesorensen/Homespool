using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// <see cref="SmtpEmailSender"/>'s STARTTLS path against Mailpit, over a genuine TLS handshake -
/// unlike <see cref="SmtpEmailSenderMailpitTests"/>, which uses <see cref="SmtpOptions.DisableTls"/>
/// and never negotiates encryption at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Requires Mailpit started via <c>./start-mailpit-tls.sh</c></b>, which generates a throwaway CA
/// and a leaf certificate signed by it (<c>generate-test-ca.sh</c>) and configures Mailpit to present
/// that leaf. The leaf is not chained to anything in the OS trust store, so these tests validate it
/// against the CA directly - <see cref="CustomCaSmtpTransportFactory"/>, an <see cref="X509Chain"/>
/// built with a custom trust anchor - rather than disabling certificate validation, and without
/// touching the OS trust store.
/// </para>
/// </remarks>
// Serialised, not parallel: all three classes share the one Mailpit container, and each clears the
// mailbox in InitializeAsync. DELETE /api/v1/messages has no filter, so a class starting up wipes
// whatever another class is mid-way through asserting on. Same pattern, and same reason, as
// [Collection("WebApplicationFactory")] in the E2E project.
[Collection("Mailpit")]
public sealed class SmtpEmailSenderStartTlsMailpitTests : IAsyncLifetime, IDisposable
{
    private readonly MailpitClient _mailpit = new();

    private static SmtpOptions MailpitStartTlsOptions() => new()
    {
        Host = "localhost",
        Port = 1025,
        UseImplicitTls = false, // StartTls - what Mailpit's default configuration offers.
        DisableTls = false,
        FromAddress = "no-reply@printerservice.test",
        FromName = "Homespool",
        TimeoutSeconds = 5,
    };

    public Task InitializeAsync() => _mailpit.ClearAsync();

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
    }

    // CA1001 wants IDisposable on a type owning a disposable field even though xUnit's IAsyncLifetime
    // already drives cleanup via DisposeAsync above; MailpitClient.Dispose is idempotent, so this is
    // a safe, redundant satisfier rather than a second real teardown path.
    public void Dispose() => _mailpit.Dispose();

    /// <summary>
    /// A message sent over a real STARTTLS handshake, validated against the CA
    /// <c>start-mailpit-tls.sh</c> generated, is actually delivered.
    /// </summary>
    [RequiresMailpitTlsFixtureFact]
    public async Task SendEmailAsyncDeliversAMessageOverAGenuineStartTlsHandshake()
    {
        // Arrange
        using X509Certificate2 trustedCa = LoadGeneratedCaCertificate();
        SmtpEmailSender sender = new(
            Options.Create(MailpitStartTlsOptions()),
            new CustomCaSmtpTransportFactory(trustedCa),
            NullLogger<SmtpEmailSender>.Instance);

        string recipient = $"starttls-{Guid.NewGuid():N}@example.com";

        // Act
        EmailSendResult result = await sender.SendEmailAsync(recipient, "Encrypted subject", "<p>Sent over STARTTLS</p>");

        // Assert
        result.Should().Be(EmailSendResult.Sent);

        MailpitClient.MailpitMessageSummary summary = await _mailpit.AwaitMessageAsync(recipient);
        MailpitClient.MailpitMessage message = await _mailpit.GetMessageAsync(summary.ID);

        message.Subject.Should().Be("Encrypted subject");
        message.HTML.Should().Contain("Sent over STARTTLS");
    }

    /// <summary>
    /// A certificate chain that doesn't lead to the trusted CA is rejected - proving
    /// <see cref="CustomCaSmtpTransportFactory"/> actually validates the chain rather than accepting
    /// anything with the right shape. Mailpit's real leaf certificate is presented either way (it
    /// isn't reconfigured for this test); what changes is which CA the client trusts.
    /// </summary>
    [RequiresMailpitTlsFixtureFact]
    public async Task SendEmailAsyncFailsWhenTheCertificateDoesNotChainToTheTrustedCa()
    {
        // Arrange
        using X509Certificate2 wrongCa = CreateThrowawayCertificateAuthority();
        SmtpEmailSender sender = new(
            Options.Create(MailpitStartTlsOptions()),
            new CustomCaSmtpTransportFactory(wrongCa),
            NullLogger<SmtpEmailSender>.Instance);

        // Act
        EmailSendResult result = await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        result.Should().Be(EmailSendResult.Failed);
    }

    /// <summary>Loads generate-test-ca.sh's CA certificate - the public half only, no private key.</summary>
    private static X509Certificate2 LoadGeneratedCaCertificate([CallerFilePath] string sourceFilePath = "")
    {
        string caCertPath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, ".mailpit-tls", "ca-cert.pem");

        if (!File.Exists(caCertPath))
        {
            // Unreachable in practice: RequiresMailpitTlsFixtureFact skips the test before it runs.
            // Kept so that calling this from somewhere without that attribute fails loudly rather than
            // with a null reference three lines later.
            throw new InvalidOperationException(
                $"CA certificate not found at {caCertPath}. Run ./start-mailpit-tls.sh first.");
        }

        return X509CertificateLoader.LoadCertificateFromFile(caCertPath);
    }

    /// <summary>
    /// A self-signed, CA-flagged certificate with no relationship to Mailpit's real one - just
    /// something a client could plausibly trust, to prove the *wrong* thing is rejected.
    /// </summary>
    private static X509Certificate2 CreateThrowawayCertificateAuthority()
    {
        using RSA rsa = RSA.Create(2048);

        CertificateRequest request = new(
            "CN=Wrong Test CA",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }
}
