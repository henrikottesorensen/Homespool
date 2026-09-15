using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using OtpNet;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

using FrameworkUserStore = Microsoft.AspNetCore.Identity.EntityFrameworkCore.UserStore<
    Homespool.Model.Entities.HSUser, Microsoft.AspNetCore.Identity.IdentityRole<long>, Homespool.Data.HomespoolDbContext, long,
    Microsoft.AspNetCore.Identity.IdentityUserClaim<long>, Microsoft.AspNetCore.Identity.IdentityUserRole<long>,
    Microsoft.AspNetCore.Identity.IdentityUserLogin<long>, Microsoft.AspNetCore.Identity.IdentityUserToken<long>,
    Microsoft.AspNetCore.Identity.IdentityRoleClaim<long>, Microsoft.AspNetCore.Identity.IdentityUserPasskey<long>>;

namespace Homespool.Host.Test;

/// <summary>
/// The two second-factor secrets at rest: what <see cref="HSUserStore"/> writes to
/// <c>AspNetUserTokens</c>, and that what the framework's own store wrote there still works.
/// </summary>
/// <remarks>
/// <b>Every at-rest assertion reads the rows past <see cref="UserManager{TUser}"/>.</b> Asking the
/// manager what it stored answers with whatever it hands back on the way out, which is the question
/// rather than the answer.
/// </remarks>
public sealed class HSUserStoreTests : IDisposable
{
    private const string FrameworkKey = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-userstore-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ---------- the authenticator key ----------
    [Fact]
    public async Task TheAuthenticatorKeyIsNotStoredAsGivenAndStillVerifiesCodes()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();

        (await rig.Users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
        string key = (await rig.Users.GetAuthenticatorKeyAsync(user))!;
        string code = new Totp(Base32Encoding.ToBytes(key)).ComputeTotp();

        key.Should().MatchRegex("^[A-Z2-7]{32}$", "the manager hands back the key an authenticator app is given");
        (await rig.StoredValuesAsync(user)).Should().NotBeEmpty()
                                            .And.AllSatisfy(value => value.Should().NotContain(key));
        (await rig.Users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code))
            .Should().BeTrue("a code computed from the key must verify against what was stored");
    }

    [Fact]
    public async Task AKeyTheFrameworkStoreWroteIsReadAsItIs()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();

        await rig.WriteAsTheFrameworkAsync(store => store.SetAuthenticatorKeyAsync(user, FrameworkKey, TestContext.Current.CancellationToken));

        (await rig.Users.GetAuthenticatorKeyAsync(user)).Should().Be(FrameworkKey);
    }

    /// <summary>A lost key ring leaves the recovery codes to sign in with, so an unreadable key is unset rather than a failure.</summary>
    [Fact]
    public async Task AKeyThisDeploymentCannotDecryptReadsAsUnsetAndIsLogged()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();
        (await rig.Users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
        FakeLogger<HSUserStore> logger = new();
        using HSUserStore otherDeployment = new(rig.Context, new EphemeralDataProtectionProvider(), logger);

        string? key = await otherDeployment.GetAuthenticatorKeyAsync(user, TestContext.Current.CancellationToken);

        key.Should().BeNull();
        logger.Collector.GetSnapshot().Should().ContainSingle()
              .Which.Level.Should().Be(LogLevel.Error);
    }

    // ---------- recovery codes ----------
    [Fact]
    public async Task RecoveryCodesAreNotStoredAsGiven()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();

        string[] codes = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();

        codes.Should().HaveCount(10);
        (await rig.Users.CountRecoveryCodesAsync(user)).Should().Be(10);
        IReadOnlyList<string> stored = await rig.StoredValuesAsync(user);
        stored.Should().ContainSingle();
        foreach (string code in codes)
        {
            stored[0].Should().NotContain(code);
            stored[0].Should().NotContain(code.Replace("-", string.Empty, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ARedeemedCodeIsSpentAndTheOthersStillRedeem()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();
        string[] codes = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 3))!.ToArray();

        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[1])).Succeeded.Should().BeTrue();
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[1])).Succeeded.Should().BeFalse("a redeemed code is spent");
        (await rig.Users.CountRecoveryCodesAsync(user)).Should().Be(2);
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded.Should().BeTrue();
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[2])).Succeeded.Should().BeTrue();

        (await rig.Users.CountRecoveryCodesAsync(user)).Should().Be(0);
        (await rig.StoredValuesAsync(user)).Should().Equal(string.Empty);
    }

    [Fact]
    public async Task ACodeThatIsNotOneOfTheSetIsRefusedAndSpendsNothing()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();
        string[] codes = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 3))!.ToArray();
        string other = codes[0][..10] + (codes[0][10] == '2' ? '3' : '2');

        IdentityResult result = await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, other);

        result.Succeeded.Should().BeFalse();
        (await rig.Users.CountRecoveryCodesAsync(user)).Should().Be(3);
    }

    [Fact]
    public async Task ANewSetReplacesTheOldOne()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();
        string old = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 3))!.First();
        string fresh = (await rig.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 3))!.First();

        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, old)).Succeeded.Should().BeFalse();
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, fresh)).Succeeded.Should().BeTrue();
    }

    /// <summary>Redeeming from a plaintext list is the moment the codes left in it are in hand, so it is when they are hashed.</summary>
    [Fact]
    public async Task CodesTheFrameworkStoreWroteStillRedeemAndWhatIsLeftIsHashed()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();
        string[] codes = ["BCDFG-HJKMN", "PQRTV-WXY23", "45678-9BCDF"];
        await rig.WriteAsTheFrameworkAsync(store => store.ReplaceCodesAsync(user, codes, TestContext.Current.CancellationToken));

        (await rig.Users.CountRecoveryCodesAsync(user)).Should().Be(3);
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[1])).Succeeded.Should().BeTrue();

        (await rig.StoredValuesAsync(user)).Should().ContainSingle()
                                            .Which.Should().NotContain(codes[0]).And.NotContain(codes[2]);
        (await rig.Users.CountRecoveryCodesAsync(user)).Should().Be(2);
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[1])).Succeeded.Should().BeFalse();
        (await rig.Users.RedeemTwoFactorRecoveryCodeAsync(user, codes[2])).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task CodesStoredInAFormThisVersionCannotReadCountAsNoneAndAreLogged()
    {
        await using Rig rig = await Rig.CreateAsync(_databasePath);
        HSUser user = await rig.AddUserAsync();
        await rig.WriteAsTheFrameworkAsync(store => store.ReplaceCodesAsync(user, ["v9$1$AAAA$AAAA"], TestContext.Current.CancellationToken));
        FakeLogger<HSUserStore> logger = new();
        using HSUserStore store = new(rig.Context, new EphemeralDataProtectionProvider(), logger);

        int count = await store.CountCodesAsync(user, TestContext.Current.CancellationToken);
        bool redeemed = await store.RedeemCodeAsync(user, "v9$1$AAAA$AAAA", TestContext.Current.CancellationToken);

        count.Should().Be(0);
        redeemed.Should().BeFalse();
        logger.Collector.GetSnapshot().Should().HaveCount(2)
              .And.AllSatisfy(record => record.Level.Should().Be(LogLevel.Error));
    }

    /// <summary>A real Identity stack, with <see cref="HSUserStore"/> as the application registers it, over a migrated database.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private Rig(HomespoolDbContext context, ServiceProvider provider)
        {
            Context = context;
            _provider = provider;
        }

        public HomespoolDbContext Context { get; }

        public UserManager<HSUser> Users => _provider.GetRequiredService<UserManager<HSUser>>();

        public static async Task<Rig> CreateAsync(string databasePath)
        {
            DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                           .UseSqlite($"Data Source={databasePath}")
                                                           .Options;

            HomespoolDbContext context = new(options);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            (_, _, _, IServiceProvider provider) = IdentityTestHarness.BuildIdentityServices(context);

            return new Rig(context, (ServiceProvider)provider);
        }

        public async Task<HSUser> AddUserAsync()
        {
            HSUser user = new("owner") { Email = "owner@example.com", EmailConfirmed = true };

            (await Users.CreateAsync(user)).Succeeded.Should().BeTrue();
            _provider.GetRequiredService<IUserStore<HSUser>>().Should().BeOfType<HSUserStore>();

            return user;
        }

        /// <summary>Every token value stored for <paramref name="user"/>, read from the table rather than through a store.</summary>
        public async Task<IReadOnlyList<string>> StoredValuesAsync(HSUser user)
        {
            return await Context.UserTokens.AsNoTracking()
                                .Where(token => token.UserId == user.Id)
                                .Select(token => token.Value ?? string.Empty)
                                .ToListAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Writes through the framework's own store, as a database from before <see cref="HSUserStore"/> would hold it.</summary>
        public async Task WriteAsTheFrameworkAsync(Func<FrameworkUserStore, Task> write)
        {
            using FrameworkUserStore framework = new(Context);

            await write(framework);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await Context.DisposeAsync();
        }
    }
}
