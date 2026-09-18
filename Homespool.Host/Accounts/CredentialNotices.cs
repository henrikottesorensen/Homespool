using System;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// Mails an account's owner when a way into the account is added or taken away.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mail is the owner's signal when the change was not theirs.</b> Every one of these changes
/// needs a recent proof, but a session somebody else holds can earn one, and what it adds outlives a
/// password change. The owner's mailbox is the one place such a change is heard about outside the
/// session that made it.
/// </para>
/// <para>
/// <b>Written in the account's language and sent to the account's address</b>, not the request's:
/// whoever made the change is not necessarily the owner, and the notice is for the owner.
/// </para>
/// <para>
/// <b>A failed send undoes nothing, and the page says nothing about it.</b> The change has happened
/// either way, and many deployments have no mail server. A failure is logged, since it means the owner
/// was not told.
/// </para>
/// <para>
/// <b>A passkey's name is never in the mail.</b> Whoever adds a passkey chooses its name, so naming it
/// would let them write into the owner's inbox. A provider's display name is the administrator's, and
/// is named.
/// </para>
/// </remarks>
public sealed class CredentialNotices
{
    private readonly IEmailSender _emailSender;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<CredentialNotices> _logger;

    public CredentialNotices(IEmailSender emailSender,
                             IStringLocalizer<SharedResource> localiser,
                             ILogger<CredentialNotices> logger)
    {
        _emailSender = emailSender;
        _localiser = localiser;
        _logger = logger;
    }

    /// <summary>Mails <paramref name="user"/>'s address that <paramref name="change"/> happened, when it has an address.</summary>
    /// <param name="user">The account that changed.</param>
    /// <param name="change">What changed.</param>
    /// <param name="provider">
    /// The provider's display name, for the provider changes; ignored for the others. Encoded here.
    /// </param>
    public async Task TellAsync(HSUser user, CredentialChange change, string? provider = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        (string subjectKey, string bodyKey) = change switch
        {
            CredentialChange.ProviderLinked => ("Email_ProviderLinkedSubject", "Email_ProviderLinkedBody"),
            CredentialChange.ProviderRemoved => ("Email_ProviderRemovedSubject", "Email_ProviderRemovedBody"),
            CredentialChange.ProviderSwappedForPassword => ("Email_ProviderSwappedSubject", "Email_ProviderSwappedBody"),
            CredentialChange.PasskeyAdded => ("Email_PasskeyAddedSubject", "Email_PasskeyAddedBody"),
            CredentialChange.PasskeyRemoved => ("Email_PasskeyRemovedSubject", "Email_PasskeyRemovedBody"),
            CredentialChange.PasskeyRevoked => ("Email_PasskeyRevokedSubject", "Email_PasskeyRevokedBody"),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Not a change the owner is told about."),
        };

        if (string.IsNullOrEmpty(user.Email))
        {
            return;
        }

        string encodedProvider = HtmlEncoder.Default.Encode(provider ?? string.Empty);

        (string subject, string body) = UserCultures.InCulture(user.Language, () => (
            _localiser[subjectKey].Value,
            _localiser[bodyKey, encodedProvider].Value));

        if (await _emailSender.SendEmailAsync(user.Email, subject, body) == EmailSendResult.Failed)
        {
            _logger.LogWarning("User {UserId}'s sign-in changed ({CredentialChange}), and the notice to the owner could not be sent.",
                               user.Id,
                               change);
        }
    }
}
