using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using OtpNet;

using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The account lockout under parallel sign-ins: a burst of wrong passwords compared no more often than
/// a patient guesser's would be, and then locked out; a right password in the middle of one still
/// signing in; and the attempt a right password was counted as given back.
/// </summary>
/// <remarks>
/// <para>
/// <b>A whole Identity stack per worker</b> over one database file in WAL, so each handler loads its
/// own copy of the account row as separate requests would. <see cref="LocalSchemeRig"/> shares one
/// context across its requests, which serialises them and would show nothing.
/// </para>
/// <para>
/// Before the fix, thirty-nine parallel wrong passwords were each compared and left a count of three
/// or four with no lockout: the framework saves the count under the concurrency stamp, and all but one
/// save of each wave was refused and counted as nothing.
/// </para>
/// </remarks>
public sealed class SignInLockoutConcurrencyTests : IDisposable
{
    private const string Wrong = "not the password at all"; // betterleaks:allow

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-lockrace-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(KeysPath))
        {
            Directory.Delete(KeysPath, recursive: true);
        }
    }

    /// <summary>One key ring for every rig of a test, so a cookie or an authenticator key one wrote, another reads.</summary>
    private string KeysPath => _databasePath + "-keys";

    /// <summary>
    /// The rigs' data protection pointed at one key ring, then <paramref name="also"/>. Each rig is its
    /// own container, and by default its own keys: the pending sign-in cookie and the stored
    /// authenticator key would then be unreadable to every rig but the one that wrote them.
    /// </summary>
    private Action<IServiceCollection> SharedKeys(Action<IServiceCollection>? also = null)
    {
        return services =>
        {
            services.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(KeysPath))
                    .SetApplicationName("hs-lockrace");
            also?.Invoke(services);
        };
    }

    private static UserPasswordCredential Credential(string password)
    {
        return new UserPasswordCredential("owner", password);
    }

    /// <summary>
    /// Presents one password per worker, each through a rig of its own, all released together, and
    /// returns the results in the order of <paramref name="passwords"/>.
    /// </summary>
    private Task<AuthenticateResult[]> InParallelAsync(IReadOnlyList<string> passwords, Action<IServiceCollection>? configure = null)
    {
        return InParallelAsync(passwords.Count,
                               (rig, i) => LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential(passwords[i])),
                               configure);
    }

    /// <summary>
    /// Runs <paramref name="present"/> once per worker, each through a rig of its own, all released
    /// together, and returns the results in worker order.
    /// </summary>
    private async Task<AuthenticateResult[]> InParallelAsync(int workers,
                                                             Func<LocalSchemeRig, int, Task<AuthenticateResult>> present,
                                                             Action<IServiceCollection>? configure = null)
    {
        List<LocalSchemeRig> rigs = [];

        try
        {
            for (int i = 0; i < workers; i++)
            {
                LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath, configure);
                rigs.Add(rig);
                await rig.Context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            }

            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<AuthenticateResult>[] running = rigs.Select((rig, i) => Task.Run(async () =>
                                                                                  {
                                                                                      await gate.Task;

                                                                                      return await present(rig, i);
                                                                                  },
                                                                                  TestContext.Current.CancellationToken))
                                                     .ToArray();
            gate.SetResult();

            return await Task.WhenAll(running);
        }
        finally
        {
            foreach (LocalSchemeRig rig in rigs)
            {
                await rig.DisposeAsync();
            }
        }
    }

    private async Task<LocalSchemeRig> SeededRigAsync(Action<IServiceCollection>? configure = null)
    {
        LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath, configure);
        await rig.Context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", TestContext.Current.CancellationToken);
        await rig.AddUserAsync("owner@example.com");

        return rig;
    }

    private static async Task<(int count, DateTimeOffset? lockoutEnd)> StoredLockoutAsync(LocalSchemeRig rig)
    {
        var stored = await rig.Context.Users.AsNoTracking()
                              .Where(u => u.UserName == "owner")
                              .Select(u => new { u.AccessFailedCount, u.LockoutEnd })
                              .SingleAsync(TestContext.Current.CancellationToken);

        return (stored.AccessFailedCount, stored.LockoutEnd);
    }

    [Fact]
    public async Task ABurstOfWrongPasswordsIsComparedOnlyUpToTheLockoutAndThenLocksItOut()
    {
        await using LocalSchemeRig rig = await SeededRigAsync();
        int maxFailed = rig.Users.Options.Lockout.MaxFailedAccessAttempts;

        ComparisonCountingHasher hasher = new();

        AuthenticateResult[] results = await InParallelAsync(Enumerable.Repeat(Wrong, 39).ToArray(),
                                                             services => services.AddSingleton<IPasswordHasher<HSUser>>(hasher));

        // What is bounded is comparisons, and only the hasher sees those: the refusals below would come
        // out the same if every password were compared first and counted after.
        hasher.Comparisons.Should().Be(maxFailed, "the allowance, and the attempt that reached the threshold; the rest are refused before their password is compared");
        results.Should().AllSatisfy(result => result.Succeeded.Should().BeFalse());
        results.Count(result => result.Refusal() == SignInRefusal.Invalid)
               .Should().Be(maxFailed - 1, "the attempts before the one that locks the account are the only ones answered as plain wrong");
        results.Count(result => result.Refusal() == SignInRefusal.LockedOut).Should().Be(39 - (maxFailed - 1));

        (int count, DateTimeOffset? lockoutEnd) = await StoredLockoutAsync(rig);
        lockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow, "the burst must end in a lockout");
        count.Should().Be(0, "reaching the threshold starts the lockout and clears the count, as the framework does");
    }

    [Fact]
    public async Task ABurstOfWrongCodesIsComparedOnlyUpToTheLockoutAndThenLocksItOut()
    {
        CodeCountingAuthenticator authenticator = new();
        Action<IServiceCollection> configure = SharedKeys(services => services.Configure<IdentityOptions>(options =>
            options.Tokens.ProviderMap[TokenOptions.DefaultAuthenticatorProvider] = new TokenProviderDescriptor(typeof(CodeCountingAuthenticator))
            {
                ProviderInstance = authenticator,
            }));

        await using LocalSchemeRig rig = await SeededRigAsync(configure);
        HSUser user = await rig.Users.FindByNameAsync("owner") ?? throw new InvalidOperationException("seeded above");
        string wrong = WrongCodeFor(await rig.EnableAuthenticatorAsync(user));
        string pending = await rig.PendingTwoFactorCookieAsync(user);
        int maxFailed = rig.Users.Options.Lockout.MaxFailedAccessAttempts;

        AuthenticateResult[] results = await InParallelAsync(39,
                                                             (worker, _) => LocalSchemeRig.AuthenticateAsync(worker.NewRequest(pending), Schemes.Totp, new TotpCredential(wrong)),
                                                             configure);

        authenticator.Comparisons.Should().Be(maxFailed, "the allowance, and the attempt that reached the threshold; the rest are refused before their code is compared");
        results.Count(result => result.Refusal() == SignInRefusal.Invalid).Should().Be(maxFailed - 1);
        results.Count(result => result.Refusal() == SignInRefusal.LockedOut).Should().Be(39 - (maxFailed - 1));
        (await StoredLockoutAsync(rig)).lockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow, "the burst must end in a lockout");
    }

    [Fact]
    public async Task ARightPasswordAmongParallelWrongOnesStillSignsIn()
    {
        await using LocalSchemeRig rig = await SeededRigAsync();
        int maxFailed = rig.Users.Options.Lockout.MaxFailedAccessAttempts;

        // Fewer wrong ones than the allowance, so nothing here may refuse the right one.
        string[] passwords = [.. Enumerable.Repeat(Wrong, maxFailed - 2), LocalSchemeRig.Password];

        AuthenticateResult[] results = await InParallelAsync(passwords);

        results[^1].Succeeded.Should().BeTrue(results[^1].Failure?.Message);
        (await StoredLockoutAsync(rig)).lockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task ARightPasswordAtTheLastAllowedAttemptSignsInAndLeavesNoLockout()
    {
        await using LocalSchemeRig rig = await SeededRigAsync();
        int maxFailed = rig.Users.Options.Lockout.MaxFailedAccessAttempts;

        for (int i = 0; i < maxFailed - 1; i++)
        {
            (await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential(Wrong))).Refusal().Should().Be(SignInRefusal.Invalid);
        }

        // This attempt reaches the threshold when it is counted, before it is compared.
        AuthenticateResult right = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential(LocalSchemeRig.Password));

        right.Succeeded.Should().BeTrue(right.Failure?.Message);
        (int count, DateTimeOffset? lockoutEnd) = await StoredLockoutAsync(rig);
        count.Should().Be(0);
        lockoutEnd.Should().BeNull("the lockout was imposed by the right password's own attempt, and lifted with it");
    }

    [Fact]
    public async Task ARightPasswordOwingASecondFactorGivesItsAttemptBackAndLeavesTheCountStanding()
    {
        await using LocalSchemeRig rig = await SeededRigAsync();
        HSUser user = await rig.Users.FindByNameAsync("owner") ?? throw new InvalidOperationException("seeded above");
        await rig.EnableAuthenticatorAsync(user);
        int maxFailed = rig.Users.Options.Lockout.MaxFailedAccessAttempts;

        for (int i = 0; i < maxFailed - 1; i++)
        {
            await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential(Wrong));
        }

        AuthenticateResult right = await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential(LocalSchemeRig.Password));

        right.Succeeded.Should().BeTrue(right.Failure?.Message);
        (int count, DateTimeOffset? lockoutEnd) = await StoredLockoutAsync(rig);
        count.Should().Be(maxFailed - 1, "a second factor is still owed, so the wrong passwords before it still count");
        lockoutEnd.Should().BeNull("the attempt that reached the threshold was right, so its lockout is lifted");
    }

    [Fact]
    public async Task ASaveOfACopyLoadedBeforeAFailureIsCountedIsRefused()
    {
        await using LocalSchemeRig rig = await SeededRigAsync();
        await using LocalSchemeRig other = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser stale = await other.Users.FindByNameAsync("owner") ?? throw new InvalidOperationException("seeded above");

        await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(), Schemes.UserPassword, Credential(Wrong));

        stale.Language = "da";
        IdentityResult saved = await other.Users.UpdateAsync(stale);

        saved.Errors.Should().ContainSingle(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure),
                                            "writing the old count back over the new one is exactly the lost update");
        (await StoredLockoutAsync(rig)).count.Should().Be(1);
    }

    [Fact]
    public async Task ACopyAlreadyStaleWhenItCountsAFailureStaysStale()
    {
        await using LocalSchemeRig rig = await SeededRigAsync();
        await using LocalSchemeRig other = await LocalSchemeRig.CreateAsync(_databasePath);
        HSUser stale = await rig.Users.FindByNameAsync("owner") ?? throw new InvalidOperationException("seeded above");
        HSUser current = await other.Users.FindByNameAsync("owner") ?? throw new InvalidOperationException("seeded above");

        current.Language = "da";
        (await other.Users.UpdateAsync(current)).Succeeded.Should().BeTrue();

        await rig.Users.AccessFailedAsync(stale);

        stale.Language = "sv";
        IdentityResult saved = await rig.Users.UpdateAsync(stale);

        saved.Errors.Should().ContainSingle(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure),
                                            "adopting the counter's new stamp would let this copy write its stale columns over the other save");
    }

    [Fact]
    public async Task TheCopyThatCountedAFailureCanStillBeSaved()
    {
        await using LocalSchemeRig rig = await SeededRigAsync();
        HSUser user = await rig.Users.FindByNameAsync("owner") ?? throw new InvalidOperationException("seeded above");

        await rig.Users.AccessFailedAsync(user);

        user.Language = "da";
        IdentityResult saved = await rig.Users.UpdateAsync(user);

        saved.Succeeded.Should().BeTrue("nothing else wrote the row, so the copy that counted is current: {0}",
                                        string.Join("; ", saved.Errors.Select(error => error.Code)));
        (await StoredLockoutAsync(rig)).count.Should().Be(1);
    }

    /// <summary>
    /// A code the authenticator provider refuses for <paramref name="secret"/>: none of the codes for the
    /// steps it accepts, which are two either side of now, with a step to spare on each side for a
    /// burst that crosses a boundary.
    /// </summary>
    private static string WrongCodeFor(byte[] secret)
    {
        Totp totp = new(secret);
        DateTime now = DateTime.UtcNow;
        HashSet<string> accepted = [.. Enumerable.Range(-3, 7).Select(step => totp.ComputeTotp(now.AddSeconds(30 * step)))];

        return Enumerable.Range(0, 1_000_000)
                         .Select(candidate => candidate.ToString("D6", CultureInfo.InvariantCulture))
                         .First(candidate => !accepted.Contains(candidate));
    }

    /// <summary>The framework's authenticator provider, counting the codes it is asked to compare.</summary>
    private sealed class CodeCountingAuthenticator : AuthenticatorTokenProvider<HSUser>
    {
        private int _comparisons;

        public int Comparisons => Volatile.Read(ref _comparisons);

        public override Task<bool> ValidateAsync(string purpose, string token, UserManager<HSUser> manager, HSUser user)
        {
            Interlocked.Increment(ref _comparisons);

            return base.ValidateAsync(purpose, token, manager, user);
        }
    }

    /// <summary>
    /// Identity's own hasher, counting the comparisons against a real account's hash - not the decoy a
    /// refused attempt pays, which verifies against an account that was never saved and has no id.
    /// </summary>
    private sealed class ComparisonCountingHasher : PasswordHasher<HSUser>
    {
        private int _comparisons;

        public int Comparisons => Volatile.Read(ref _comparisons);

        public override PasswordVerificationResult VerifyHashedPassword(HSUser user, string hashedPassword, string providedPassword)
        {
            if (user.Id != 0)
            {
                Interlocked.Increment(ref _comparisons);
            }

            return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
