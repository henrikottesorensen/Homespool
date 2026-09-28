using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.Host.Notifications.WebPush;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The deployment's VAPID key: made once and kept, because every subscription is bound to it - and
/// replaced, with the subscriptions it orphans, only when it can no longer be read.
/// </summary>
public sealed class VapidKeyStoreTests : System.IDisposable
{
    private readonly string _databasePath = WebPushRig.NewDatabasePath();

    public void Dispose()
    {
        WebPushRig.Delete(_databasePath);
    }

    private static Task<VapidCredentials> KeyAsync(WebPushRig rig)
    {
        return rig.Services.GetRequiredService<VapidKeyStore>().GetAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheKeyIsMadeOnceAndIsTheSameAfterARestart()
    {
        // One provider for both rigs: the same key ring, which is what a restart keeps.
        EphemeralDataProtectionProvider keyRing = new();

        string first;

        await using (WebPushRig rig = await WebPushRig.CreateAsync(_databasePath, keyRing))
        {
            first = (await KeyAsync(rig)).PublicKey;

            (await KeyAsync(rig)).PublicKey.Should().Be(first);
        }

        await using WebPushRig restarted = await WebPushRig.CreateAsync(_databasePath, keyRing);

        (await KeyAsync(restarted)).PublicKey.Should().Be(first, "every subscription was made with this key");

        int rows = await restarted.InScopeAsync(services =>
            services.GetRequiredService<HomespoolDbContext>().VapidKeys.CountAsync(TestContext.Current.CancellationToken));

        rows.Should().Be(1);
    }

    [Fact]
    public async Task ThePrivateHalfIsNotStoredInTheClear()
    {
        await using WebPushRig rig = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider());

        await KeyAsync(rig);

        VapidKey stored = await rig.InScopeAsync(services =>
            services.GetRequiredService<HomespoolDbContext>().VapidKeys.SingleAsync(TestContext.Current.CancellationToken));

        stored.PrivateKeySecret.Length.Should().BeGreaterThan(43, "a bare 32-byte scalar is 43 base64url characters; ciphertext is longer");
        new EphemeralDataProtectionProvider().CreateProtector(VapidKeyStore.Purpose)
                                             .Invoking(protector => protector.Unprotect(stored.PrivateKeySecret))
                                             .Should().Throw<System.Security.Cryptography.CryptographicException>(
                                                 "another deployment's key ring cannot read it");
    }

    /// <summary>
    /// The key ring lost: the old key is unreadable, so it is replaced - and the subscriptions made with
    /// it go too, since no push service would accept a request for them signed by the new one.
    /// </summary>
    [Fact]
    public async Task AKeyThisDeploymentCannotReadIsReplacedAndItsSubscriptionsRemoved()
    {
        string lost;

        await using (WebPushRig before = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider()))
        {
            lost = (await KeyAsync(before)).PublicKey;

            HSUser user = await before.AddUserAsync("owner@example.com");
            using FakePushBrowser browser = FakePushService.NewBrowser();
            await before.AddBrowserAsync(user.Id, browser);
        }

        await using WebPushRig after = await WebPushRig.CreateAsync(_databasePath, new EphemeralDataProtectionProvider());

        string replacement = (await KeyAsync(after)).PublicKey;

        replacement.Should().NotBe(lost);

        int subscriptions = await after.InScopeAsync(services =>
            services.GetRequiredService<HomespoolDbContext>().WebPushDestinations.CountAsync(TestContext.Current.CancellationToken));

        subscriptions.Should().Be(0, "a subscription made with the lost key can never be delivered to");
    }
}
