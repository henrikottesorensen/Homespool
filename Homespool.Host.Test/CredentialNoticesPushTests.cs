using System;
using System.Text.Json;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.Accounts;
using Homespool.Host.Mail;
using Homespool.Host.Notifications;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// With no mail server the owner's browsers are told of a change to how the account is signed into;
/// with one, mail is the notice and a push is not added to it.
/// </summary>
public sealed class CredentialNoticesPushTests : IAsyncLifetime
{
    private readonly string _databasePath = WebPushRig.NewDatabasePath();
    private readonly CapturingEmailSender _mail = new();
    private WebPushRig _rig = null!;

    public async ValueTask InitializeAsync()
    {
        _rig = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await _rig.DisposeAsync();
        WebPushRig.Delete(_databasePath);
    }

    private Task TellAsync(HSUser user, CredentialChange change)
    {
        return _rig.InScopeAsync(async services =>
        {
            CredentialNotices notices = new(_mail,
                                            TestLocaliser.Shared(),
                                            NullLogger<CredentialNotices>.Instance,
                                            services.GetRequiredService<NotificationDestinationService>());

            await notices.TellAsync(user, change, cancellationToken: TestContext.Current.CancellationToken);

            return 0;
        });
    }

    [Fact]
    public async Task WithNoMailServerTheOwnersBrowserIsTold()
    {
        // Arrange
        _mail.Result = EmailSendResult.NotConfigured;
        HSUser owner = await _rig.AddUserAsync("owner@example.com", language: "da");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        await _rig.AddBrowserAsync(owner.Id, browser);

        // Act
        await TellAsync(owner, CredentialChange.ApiTokenIssued);

        // Assert
        JsonElement payload = browser.DecryptJson(_rig.PushService.Received.Should().ContainSingle().Subject.Body);
        payload.GetProperty("title").GetString().Should().Be("Der er udstedt et nyt API-token til din Homespool-konto");
        payload.GetProperty("url").GetString().Should().Be("/Account/Manage/ApiTokens");
    }

    [Fact]
    public async Task AMailThatWasSentIsNotAlsoPushed()
    {
        // Arrange
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        await _rig.AddBrowserAsync(owner.Id, browser);

        // Act
        await TellAsync(owner, CredentialChange.ApiTokenIssued);

        // Assert
        _mail.SentEmails.Should().ContainSingle();
        _rig.PushService.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task AMailThatFailedIsNotPushedInItsPlace()
    {
        // Arrange
        _mail.Result = EmailSendResult.Failed;
        HSUser owner = await _rig.AddUserAsync("owner@example.com");
        using FakePushBrowser browser = FakePushService.NewBrowser();
        await _rig.AddBrowserAsync(owner.Id, browser);

        // Act
        await TellAsync(owner, CredentialChange.ApiTokenIssued);

        // Assert
        _rig.PushService.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAccountWithNoBrowserAndNoMailServerIsNotAnError()
    {
        // Arrange
        _mail.Result = EmailSendResult.NotConfigured;
        HSUser owner = await _rig.AddUserAsync("owner@example.com");

        // Act
        Func<Task> act = () => TellAsync(owner, CredentialChange.ApiTokenIssued);

        // Assert
        await act.Should().NotThrowAsync();
        _rig.PushService.Received.Should().BeEmpty();
    }
}
