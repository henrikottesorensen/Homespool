using AwesomeAssertions;

using Microsoft.Extensions.Options;

using Homespool.Host.Mail;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The rule deciding whether a newly created account is confirmed at creation. It is security-relevant
/// and shared across every creation path, so both branches are pinned: confirm when no mail can be
/// sent, do not confirm when it can.
/// </summary>
public class AccountConfirmationPolicyTests
{
    private static AccountConfirmationPolicy PolicyFor(string host)
    {
        return new AccountConfirmationPolicy(Options.Create(new SmtpOptions { Host = host }));
    }

    /// <summary>
    /// With no SMTP host, a confirmation mail can never arrive, so the account must be confirmed at
    /// creation - otherwise <c>RequireConfirmedAccount</c> would leave it permanently unable to sign in.
    /// </summary>
    [Fact]
    public void ConfirmsAtCreationWhenSmtpIsNotConfigured()
    {
        // Arrange
        AccountConfirmationPolicy policy = PolicyFor(host: string.Empty);
        HSUser user = new();

        // Act
        policy.Apply(user);

        // Assert
        user.EmailConfirmed.Should().BeTrue();
    }

    /// <summary>
    /// With SMTP configured, the account stays unconfirmed so the real confirmation flow runs. The
    /// user starts pre-set to confirmed to prove <see cref="AccountConfirmationPolicy.Apply"/> actively
    /// clears it rather than merely leaving the default.
    /// </summary>
    [Fact]
    public void DoesNotConfirmAtCreationWhenSmtpIsConfigured()
    {
        // Arrange
        AccountConfirmationPolicy policy = PolicyFor(host: "smtp.example.com");
        HSUser user = new() { EmailConfirmed = true };

        // Act
        policy.Apply(user);

        // Assert
        user.EmailConfirmed.Should().BeFalse();
    }
}
