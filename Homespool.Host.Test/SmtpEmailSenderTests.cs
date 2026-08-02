using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MimeKit;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="SmtpEmailSender"/> against a hand-rolled <see cref="FakeSmtpTransport"/> rather than a
/// real (or fake) SMTP server. MailKit's own wire-protocol correctness is trusted as given; what
/// this verifies is <em>our</em> code - which socket options get chosen, whether auth is attempted,
/// the envelope actually sent, and how the result maps to <see cref="EmailSendResult"/>.
/// </summary>
public sealed class SmtpEmailSenderTests
{
    private static (SmtpEmailSender sender, FakeSmtpTransport transport) NewSender(SmtpOptions? options = null)
    {
        FakeSmtpTransport transport = new();
        FakeSmtpTransportFactory factory = new(transport);

        SmtpEmailSender sender = new(Options.Create(options ?? DefaultOptions()), factory, NullLogger<SmtpEmailSender>.Instance);

        return (sender, transport);
    }

    private static SmtpOptions DefaultOptions() => new()
    {
        Host = "smtp.example.com",
        Port = 587,
        FromAddress = "no-reply@example.com",
        FromName = "Homespool",
        TimeoutSeconds = 5,
    };

    // ---------- envelope ----------

    /// <summary>The message actually handed to the transport carries the right from/to/subject/body.</summary>
    [Fact]
    public async Task SendEmailAsyncBuildsTheCorrectEnvelope()
    {
        // Arrange
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender();

        // Act
        EmailSendResult result = await sender.SendEmailAsync("recipient@example.com", "Confirm your email", "<p>Hello</p>");

        // Assert
        result.Should().Be(EmailSendResult.Sent);

        transport.SentMessage.Should().NotBeNull();
        transport.SentMessage!.From.Mailboxes.Single().Address.Should().Be("no-reply@example.com");
        transport.SentMessage.From.Mailboxes.Single().Name.Should().Be("Homespool");
        transport.SentMessage.To.Mailboxes.Single().Address.Should().Be("recipient@example.com");
        transport.SentMessage.Subject.Should().Be("Confirm your email");
        transport.SentHtmlBody.Should().Be("<p>Hello</p>");
    }

    /// <summary>An empty FromAddress falls back to UserName, per SmtpOptions.ResolvedFromAddress.</summary>
    [Fact]
    public async Task SendEmailAsyncUsesUserNameAsFromAddressWhenFromAddressIsEmpty()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.FromAddress = string.Empty;
        options.UserName = "relay-account@example.com";
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender(options);

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.SentMessage!.From.Mailboxes.Single().Address.Should().Be("relay-account@example.com");
    }

    // ---------- socket options ----------

    /// <summary>UseImplicitTls false (the default) requires StartTLS - never a silent plaintext fallback.</summary>
    [Fact]
    public async Task SendEmailAsyncRequiresStartTlsWhenImplicitTlsIsDisabled()
    {
        // Arrange
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender();

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.ConnectCall!.Value.options.Should().Be(SecureSocketOptions.StartTls);
    }

    /// <summary>UseImplicitTls true connects already encrypted, for port 465-style servers.</summary>
    [Fact]
    public async Task SendEmailAsyncUsesSslOnConnectWhenImplicitTlsIsEnabled()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.UseImplicitTls = true;
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender(options);

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.ConnectCall!.Value.options.Should().Be(SecureSocketOptions.SslOnConnect);
    }

    /// <summary>DisableTls is the explicit, separate opt-in for a trusted unencrypted relay (e.g. Mailpit).</summary>
    [Fact]
    public async Task SendEmailAsyncConnectsUnencryptedOnlyWhenDisableTlsIsExplicitlySet()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.DisableTls = true;
        options.UseImplicitTls = true; // DisableTls must win regardless of UseImplicitTls.
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender(options);

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.ConnectCall!.Value.options.Should().Be(SecureSocketOptions.None);
    }

    /// <summary>Connect is always given the configured host and port.</summary>
    [Fact]
    public async Task SendEmailAsyncConnectsToTheConfiguredHostAndPort()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.Host = "mail.internal";
        options.Port = 2525;
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender(options);

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.ConnectCall!.Value.host.Should().Be("mail.internal");
        transport.ConnectCall!.Value.port.Should().Be(2525);
    }

    // ---------- authentication ----------

    /// <summary>A configured username authenticates with the configured credentials.</summary>
    [Fact]
    public async Task SendEmailAsyncAuthenticatesWhenAUserNameIsConfigured()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.UserName = "relay-account";
        options.Password = "hunter2";
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender(options);

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.AuthenticateCall.Should().NotBeNull();
        transport.AuthenticateCall!.Value.userName.Should().Be("relay-account");
        transport.AuthenticateCall.Value.password.Should().Be("hunter2");
    }

    /// <summary>No username configured means no authentication attempt - an unauthenticated relay is valid.</summary>
    [Fact]
    public async Task SendEmailAsyncDoesNotAuthenticateWhenNoUserNameIsConfigured()
    {
        // Arrange
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender();

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.AuthenticateCall.Should().BeNull();
    }

    // ---------- disconnect ----------

    /// <summary>A successful send disconnects cleanly (QUIT) rather than abandoning the connection.</summary>
    [Fact]
    public async Task SendEmailAsyncDisconnectsAfterASuccessfulSend()
    {
        // Arrange
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender();

        // Act
        await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        transport.DisconnectedWithQuit.Should().Be(true);
    }

    // ---------- failure mapping ----------

    /// <summary>A failure connecting is reported, not thrown.</summary>
    [Fact]
    public async Task SendEmailAsyncReturnsFailedWhenConnectThrows()
    {
        // Arrange
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender();
        transport.ThrowOnConnect = new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.ServiceNotAvailable, "down");

        // Act
        EmailSendResult result = await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        result.Should().Be(EmailSendResult.Failed);
    }

    /// <summary>A failure authenticating is reported, not thrown.</summary>
    [Fact]
    public async Task SendEmailAsyncReturnsFailedWhenAuthenticateThrows()
    {
        // Arrange
        SmtpOptions options = DefaultOptions();
        options.UserName = "relay-account";
        options.Password = "wrong";
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender(options);
        transport.ThrowOnAuthenticate = new AuthenticationException("bad credentials");

        // Act
        EmailSendResult result = await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        result.Should().Be(EmailSendResult.Failed);
    }

    /// <summary>A failure sending is reported, not thrown.</summary>
    [Fact]
    public async Task SendEmailAsyncReturnsFailedWhenSendThrows()
    {
        // Arrange
        (SmtpEmailSender sender, FakeSmtpTransport transport) = NewSender();
        transport.ThrowOnSend = new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "rejected");

        // Act
        EmailSendResult result = await sender.SendEmailAsync("recipient@example.com", "Subject", "Body");

        // Assert
        result.Should().Be(EmailSendResult.Failed);
    }
}
