using System;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

using Homespool.Host.Localisation;
using Homespool.Host.Mail;
using Homespool.Host.Notifications;
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
/// <b>A passkey's name, or an API token's, is never in the mail.</b> Whoever adds one chooses its name,
/// so naming it would let them write into the owner's inbox. A provider's display name is the administrator's, and
/// is named.
/// </para>
/// <para>
/// <b>With no mail server, the owner's browsers are told instead.</b> A deployment without SMTP has no
/// mailbox to hear it in, and a notice nobody can receive is no notice. The same words go out as a
/// browser push to every browser the owner has subscribed, with a plain sentence for a body since a
/// push has no HTML. A server that is configured but failed is not this case: the mail was meant to
/// go, the failure is logged, and a push in its place would say a different thing in a different place.
/// </para>
/// </remarks>
public sealed class CredentialNotices
{
    private readonly IEmailSender _emailSender;
    private readonly IStringLocalizer<SharedResource> _localiser;
    private readonly ILogger<CredentialNotices> _logger;
    private readonly NotificationDestinationService? _push;

    /// <summary>Mails, or pushes, the notices of changes to an account's sign-in.</summary>
    /// <param name="emailSender">Sends the mail.</param>
    /// <param name="localiser">Words the notice.</param>
    /// <param name="logger">Records a notice that could not be sent.</param>
    /// <param name="push">
    /// Where the notice goes when there is no mail server. Null where nothing can be pushed, as in a
    /// test that is not about it.
    /// </param>
    public CredentialNotices(IEmailSender emailSender,
                             IStringLocalizer<SharedResource> localiser,
                             ILogger<CredentialNotices> logger,
                             NotificationDestinationService? push = null)
    {
        _emailSender = emailSender;
        _localiser = localiser;
        _logger = logger;
        _push = push;
    }

    /// <summary>Mails <paramref name="user"/>'s address that <paramref name="change"/> happened, when it has an address.</summary>
    /// <param name="user">The account that changed.</param>
    /// <param name="change">What changed.</param>
    /// <param name="provider">
    /// The provider's display name, for the provider changes; ignored for the others. Encoded here.
    /// </param>
    /// <param name="cancellationToken">Cancels the browser push.</param>
    public async Task TellAsync(HSUser user, CredentialChange change, string? provider = null, CancellationToken cancellationToken = default)
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
            CredentialChange.ApiTokenIssued => ("Email_ApiTokenIssuedSubject", "Email_ApiTokenIssuedBody"),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Not a change the owner is told about."),
        };

        if (string.IsNullOrEmpty(user.Email))
        {
            await PushAsync(user, change, subjectKey, cancellationToken);

            return;
        }

        string encodedProvider = HtmlEncoder.Default.Encode(provider ?? string.Empty);

        (string subject, string body) = UserCultures.InCulture(user.Language, () => (
            _localiser[subjectKey].Value,
            _localiser[bodyKey, encodedProvider].Value));

        EmailSendResult sent = await _emailSender.SendEmailAsync(user.Email, subject, body);

        if (sent == EmailSendResult.Failed)
        {
            _logger.LogWarning("User {UserId}'s sign-in changed ({CredentialChange}), and the notice to the owner could not be sent.",
                               user.Id,
                               change);
        }
        else if (sent == EmailSendResult.NotConfigured)
        {
            await PushAsync(user, change, subjectKey, cancellationToken);
        }
    }

    /// <summary>
    /// Tells <paramref name="user"/>'s subscribed browsers, in the account's language. Like the mail, a
    /// failure undoes nothing and is not thrown.
    /// </summary>
    private async Task PushAsync(HSUser user, CredentialChange change, string subjectKey, CancellationToken cancellationToken)
    {
        if (_push is null)
        {
            return;
        }

        NotificationMessage message = UserCultures.InCulture(user.Language, () => new NotificationMessage(
            _localiser[subjectKey].Value,
            _localiser["Push_CredentialChangeBody"].Value,
            change switch
            {
                CredentialChange.ApiTokenIssued => "/Account/Manage/ApiTokens",
                CredentialChange.PasskeyAdded or CredentialChange.PasskeyRemoved or CredentialChange.PasskeyRevoked => "/Account/Manage/Passkeys",
                _ => "/Account/Manage/ExternalLogins",
            },
            "homespool-credential-change",
            NotificationUrgency.High,
            TimeSpan.FromHours(24)));

        try
        {
            await _push.DeliverToAllAsync(user.Id, message, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "User {UserId}'s sign-in changed ({CredentialChange}), and the browser notice to the owner could not be sent.",
                               user.Id,
                               change);
        }
    }
}
