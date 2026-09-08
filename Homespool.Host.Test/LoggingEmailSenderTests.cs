using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Mail;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="LoggingEmailSender"/>, which stands in for the real sender when no SMTP server is configured.
/// What matters is as much what it does not write as what it does: the bodies it is handed carry password-reset,
/// address-confirmation and invitation links, and a link like that is a credential.
/// </summary>
public sealed class LoggingEmailSenderTests
{
    private const string ResetLink = "https://homespool.example.net/Account/ResetPassword?code=CfDJ8NrAt-secret-token";

    /// <summary>The caller is told nothing was sent, which is normal rather than a failure without a mail server.</summary>
    [Fact]
    public async Task SendEmailAsyncReportsNotConfigured()
    {
        // Arrange
        FakeLogger<LoggingEmailSender> logger = new();
        LoggingEmailSender sender = new(logger);

        // Act
        EmailSendResult result = await sender.SendEmailAsync("someone@example.com", "Reset your password", ResetLink);

        // Assert
        result.Should().Be(EmailSendResult.NotConfigured);
    }

    /// <summary>The attempt is recorded with its recipient and subject, which is what a non-disclosure test reads.</summary>
    [Fact]
    public async Task SendEmailAsyncRecordsTheRecipientAndSubject()
    {
        // Arrange
        FakeLogger<LoggingEmailSender> logger = new();
        LoggingEmailSender sender = new(logger);

        // Act
        await sender.SendEmailAsync("someone@example.com", "Reset your password", ResetLink);

        // Assert
        FakeLogRecord record = logger.Collector.GetSnapshot().Should().ContainSingle().Subject;

        record.Level.Should().Be(LogLevel.Information);
        record.StructuredState.Should().Contain(property => property.Key == "Email" && property.Value == "someone@example.com");
        record.StructuredState.Should().Contain(property => property.Key == "Subject" && property.Value == "Reset your password");
    }

    /// <summary>
    /// No record carries the body, at any level. <see cref="FakeLogger{T}"/> collects every level regardless of
    /// configuration, so this holds however verbose a deployment is turned up - which is the point: raising the
    /// level to diagnose a sign-in problem must not start collecting live reset tokens.
    /// </summary>
    [Fact]
    public async Task SendEmailAsyncNeverRecordsTheBody()
    {
        // Arrange
        FakeLogger<LoggingEmailSender> logger = new();
        LoggingEmailSender sender = new(logger);

        // Act
        await sender.SendEmailAsync("someone@example.com", "Reset your password", ResetLink);

        // Assert
        foreach (FakeLogRecord record in logger.Collector.GetSnapshot())
        {
            record.Message.Should().NotContain(ResetLink);
            record.StructuredState.Should().NotContain(
                property => property.Value != null && property.Value.Contains(ResetLink, StringComparison.Ordinal));
        }
    }
}
