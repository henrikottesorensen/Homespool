using System;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Host.Accounts;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The owner hears about every change to how their account is signed into, in their own language,
/// and a mail that cannot be sent is logged rather than thrown.
/// </summary>
public sealed class CredentialNoticesTests
{
    private readonly CapturingEmailSender _mail = new();
    private readonly FakeLogger<CredentialNotices> _logger = new();

    private CredentialNotices Notices => new(_mail, TestLocaliser.Shared(), _logger);

    private static HSUser Owner(string? language = null)
    {
        return new HSUser("owner")
        {
            Id = 42,
            Email = "owner@example.com",
            Language = language,
        };
    }

    [Theory]
    [InlineData(CredentialChange.ProviderLinked, "A sign-in provider was linked to your Homespool account")]
    [InlineData(CredentialChange.ProviderRemoved, "A sign-in provider was removed from your Homespool account")]
    [InlineData(CredentialChange.ProviderSwappedForPassword, "Your Homespool account now signs in with a password")]
    [InlineData(CredentialChange.PasskeyAdded, "A passkey was added to your Homespool account")]
    [InlineData(CredentialChange.PasskeyRemoved, "A passkey was removed from your Homespool account")]
    [InlineData(CredentialChange.PasskeyRevoked, "An administrator removed a passkey from your Homespool account")]
    public async Task EachChangeIsMailedToTheAccountsAddressUnderItsOwnSubject(CredentialChange change, string subject)
    {
        // Act
        await Notices.TellAsync(Owner(), change, "Dex");

        // Assert
        (string email, string subject, string body) sent = _mail.SentEmails.Should().ContainSingle().Subject;
        sent.email.Should().Be("owner@example.com");
        sent.subject.Should().Be(subject);
        sent.body.Should().Contain("contact the Homespool administrator").And.NotContain("{0}");
    }

    /// <summary>Whoever made the change is not necessarily the owner, so the request's language is not the one to write in.</summary>
    [Fact]
    public async Task TheNoticeIsWrittenInTheAccountsLanguage()
    {
        // Act
        await Notices.TellAsync(Owner("da"), CredentialChange.PasskeyAdded);

        // Assert
        _mail.SentEmails.Should().ContainSingle().Which.subject.Should().Be("En adgangsnøgle er blevet tilføjet til din Homespool-konto");
    }

    /// <summary>The provider is named, and a display name is text in an HTML body like any other.</summary>
    [Fact]
    public async Task TheProviderIsNamedAndEncoded()
    {
        // Act
        await Notices.TellAsync(Owner(), CredentialChange.ProviderSwappedForPassword, "Dex <b>");

        // Assert
        string body = _mail.SentEmails.Should().ContainSingle().Subject.htmlMessage;
        body.Should().StartWith("Dex &lt;b&gt; was just removed").And.Contain("signing in with Dex &lt;b&gt; no longer works");
        body.Should().NotContain("<b>");
    }

    [Fact]
    public async Task AnAccountWithNoAddressIsNotMailed()
    {
        // Arrange
        HSUser owner = Owner();
        owner.Email = null;

        // Act
        await Notices.TellAsync(owner, CredentialChange.PasskeyAdded);

        // Assert
        _mail.SentEmails.Should().BeEmpty();
    }

    /// <summary>The change has happened either way; a notice that did not go is what the log is for.</summary>
    [Fact]
    public async Task ANoticeThatCannotBeSentIsLoggedNotThrown()
    {
        // Arrange
        _mail.Result = EmailSendResult.Failed;

        // Act
        await Notices.TellAsync(Owner(), CredentialChange.ProviderLinked, "Dex");

        // Assert
        FakeLogRecord record = _logger.Collector.GetSnapshot().Should().ContainSingle().Subject;
        record.Level.Should().Be(LogLevel.Warning);
        record.StructuredState.Should().Contain(property => property.Key == "UserId" && property.Value == "42");
        record.StructuredState.Should().Contain(property => property.Key == "CredentialChange" && property.Value == nameof(CredentialChange.ProviderLinked));
    }

    [Fact]
    public async Task AnUnassignedChangeIsRefused()
    {
        // Act
        Func<Task> tell = () => Notices.TellAsync(Owner(), CredentialChange.Undefined);

        // Assert
        await tell.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _mail.SentEmails.Should().BeEmpty();
    }
}
