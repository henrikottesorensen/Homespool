using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Homespool.Host.Services;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// <see cref="SmtpEmailSender"/> against a real SMTP server - <a href="https://mailpit.axllent.org/">Mailpit</a>
/// - rather than <c>Homespool.Host.Test</c>'s mocked <c>FakeSmtpTransport</c>. Where that
/// project trusts MailKit's wire-protocol correctness and verifies only our code, this project
/// exists to verify the two actually agree: a message really is deliverable, end to end, through a
/// real (if disposable) mail server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Requires a running Mailpit container</b> - <c>./start-mailpit-tls.sh</c> (SMTP on 1025, its
/// HTTP API on 8025). Not started or managed by this test project; there is no fake or in-process
/// substitute here, by design, since faking the server would just be
/// <c>Homespool.Host.Test</c>'s job again.
/// </para>
/// <para>
/// This class uses <see cref="SmtpOptions.DisableTls"/> - the plaintext path, exercised regardless
/// of whether Mailpit currently offers STARTTLS. See <see cref="SmtpEmailSenderStartTlsMailpitTests"/>
/// for the encrypted path, which needs the CA <c>start-mailpit-tls.sh</c> generates.
/// </para>
/// </remarks>
// Serialised, not parallel: all three classes share the one Mailpit container, and each clears the
// mailbox in InitializeAsync. DELETE /api/v1/messages has no filter, so a class starting up wipes
// whatever another class is mid-way through asserting on. Same pattern, and same reason, as
// [Collection("WebApplicationFactory")] in the E2E project.
[Collection("Mailpit")]
public sealed class SmtpEmailSenderMailpitTests : IAsyncLifetime, IDisposable
{
    private readonly MailpitClient _mailpit = new();

    private static SmtpOptions MailpitOptions() => new()
    {
        Host = "localhost",
        Port = 1025,
        DisableTls = true,
        FromAddress = "no-reply@printerservice.test",
        FromName = "Homespool",
        TimeoutSeconds = 5,
    };

    private static SmtpEmailSender NewSender(SmtpOptions options) =>
        new(Options.Create(options), new MailKitSmtpTransportFactory(), NullLogger<SmtpEmailSender>.Instance);

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
    /// A message sent through <see cref="SmtpEmailSender"/> is actually delivered - not just
    /// reported as sent, but retrievable from the mail server with the right recipient, subject and
    /// HTML body.
    /// </summary>
    [Fact]
    public async Task SendEmailAsyncDeliversARealMessageThroughMailpit()
    {
        // Arrange
        SmtpEmailSender sender = NewSender(MailpitOptions());
        string recipient = $"recipient-{Guid.NewGuid():N}@example.com";

        // Act
        EmailSendResult result = await sender.SendEmailAsync(recipient, "Confirm your email", "<p>Hello from the integration test</p>");

        // Assert
        result.Should().Be(EmailSendResult.Sent);

        MailpitClient.MailpitMessageSummary summary = await _mailpit.AwaitMessageAsync(recipient);
        MailpitClient.MailpitMessage message = await _mailpit.GetMessageAsync(summary.ID);

        message.Subject.Should().Be("Confirm your email");
        message.To.Should().ContainSingle().Which.Address.Should().Be(recipient);
        message.HTML.Should().Contain("Hello from the integration test");
    }

    /// <summary>
    /// A second message to a different recipient in the same test run does not get confused with the
    /// first - Mailpit is a shared mailbox, so delivery has to be matched by recipient, not just "is
    /// there anything in the inbox".
    /// </summary>
    [Fact]
    public async Task SendEmailAsyncDeliversDistinctMessagesToDistinctRecipients()
    {
        // Arrange
        SmtpEmailSender sender = NewSender(MailpitOptions());
        string first = $"first-{Guid.NewGuid():N}@example.com";
        string second = $"second-{Guid.NewGuid():N}@example.com";

        // Act
        await sender.SendEmailAsync(first, "First subject", "<p>First</p>");
        await sender.SendEmailAsync(second, "Second subject", "<p>Second</p>");

        // Assert
        MailpitClient.MailpitMessage firstMessage = await _mailpit.GetMessageAsync((await _mailpit.AwaitMessageAsync(first)).ID);
        MailpitClient.MailpitMessage secondMessage = await _mailpit.GetMessageAsync((await _mailpit.AwaitMessageAsync(second)).ID);

        firstMessage.Subject.Should().Be("First subject");
        secondMessage.Subject.Should().Be("Second subject");
    }

    /// <summary>
    /// A genuinely unreachable server is reported as failed, not thrown - checked against a real
    /// closed port rather than a mock, so this exercises MailKit's actual connect-failure behaviour
    /// too, not just our exception handler shape.
    /// </summary>
    [Fact]
    public async Task SendEmailAsyncReturnsFailedWhenTheServerIsUnreachable()
    {
        // Arrange
        SmtpOptions options = MailpitOptions();
        options.Port = 1; // Reserved, nothing listens here.
        options.TimeoutSeconds = 2;
        SmtpEmailSender sender = NewSender(options);

        // Act
        EmailSendResult result = await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        result.Should().Be(EmailSendResult.Failed);
    }
}
