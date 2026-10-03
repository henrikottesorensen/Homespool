using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using OtpNet;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// An authenticator code is accepted once: a right code spends its time step, and a code for that step
/// or an earlier one is refused from then on, for a sign-in and a step-up alike.
/// </summary>
/// <remarks>
/// <b>The host's clock is fixed halfway through a step</b>, and every code is computed by Otp.NET at an
/// offset from it, so which step a code belongs to is decided by the test and never by when it runs.
/// </remarks>
public sealed class AuthenticatorCodeReplayTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-totp-replay-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 15, TimeSpan.Zero));

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

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds() / 30;

    private Task<LocalSchemeRig> CreateRigAsync()
    {
        return LocalSchemeRig.CreateAsync(_databasePath, services => services.AddSingleton<TimeProvider>(_clock));
    }

    /// <summary>The code <paramref name="secret"/>'s app shows <paramref name="steps"/> steps from the host's now.</summary>
    private string CodeAt(byte[] secret, int steps)
    {
        return new Totp(secret).ComputeTotp(_clock.GetUtcNow().UtcDateTime.AddSeconds(30 * steps));
    }

    private static async Task<long?> StoredStepAsync(LocalSchemeRig rig, HSUser user)
    {
        return await rig.Context.Users.AsNoTracking()
                        .Where(u => u.Id == user.Id)
                        .Select(u => u.AuthenticatorStepUsed)
                        .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Presents <paramref name="code"/> as a sign-in's second factor, or as a step-up.</summary>
    private static async Task<AuthenticateResult> PresentAsync(LocalSchemeRig rig, HSUser user, string code, bool stepUp)
    {
        return stepUp ?
            await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(await rig.SessionCookieAsync(user)), Schemes.Totp, new TotpStepUpCredential(code)) :
            await LocalSchemeRig.AuthenticateAsync(rig.NewRequest(await rig.PendingTwoFactorCookieAsync(user)), Schemes.Totp, new TotpCredential(code));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARightCodeIsAcceptedOnce(bool stepUp)
    {
        await using LocalSchemeRig rig = await CreateRigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string code = CodeAt(secret, 0);

        AuthenticateResult first = await PresentAsync(rig, user, code, stepUp);
        AuthenticateResult again = await PresentAsync(rig, user, code, stepUp);

        first.Succeeded.Should().BeTrue(first.Failure?.Message);
        again.Succeeded.Should().BeFalse("the code was spent by the first presentation, and is still inside the window");
        again.Refusal().Should().Be(SignInRefusal.Invalid);

        _clock.Advance(TimeSpan.FromSeconds(30));
        AuthenticateResult next = await PresentAsync(rig, user, CodeAt(secret, 0), stepUp);

        next.Succeeded.Should().BeTrue("the next step's code has not been used: {0}", next.Failure?.Message);
    }

    [Fact]
    public async Task ACodeUsedForASignInIsRefusedForAStepUp()
    {
        await using LocalSchemeRig rig = await CreateRigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string code = CodeAt(secret, 0);

        (await PresentAsync(rig, user, code, stepUp: false)).Succeeded.Should().BeTrue();
        AuthenticateResult stepUp = await PresentAsync(rig, user, code, stepUp: true);

        stepUp.Succeeded.Should().BeFalse("one code serves one act, whichever scheme it was presented to");
    }

    [Fact]
    public async Task ACodeForAnEarlierStepIsRefusedOnceALaterOneIsAccepted()
    {
        await using LocalSchemeRig rig = await CreateRigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);

        AuthenticateResult earlier = await PresentAsync(rig, user, CodeAt(secret, -1), stepUp: false);
        AuthenticateResult later = await PresentAsync(rig, user, CodeAt(secret, 1), stepUp: false);
        AuthenticateResult between = await PresentAsync(rig, user, CodeAt(secret, 0), stepUp: false);

        earlier.Succeeded.Should().BeTrue(earlier.Failure?.Message);
        later.Succeeded.Should().BeTrue("a later step than the one spent is still accepted: {0}", later.Failure?.Message);
        between.Succeeded.Should().BeFalse("its step is inside the window but before the one already spent");
        (await StoredStepAsync(rig, user)).Should().Be(Now + 1, "the step spent is the one the code matched, not the clock's");
    }

    /// <summary>The framework's window, kept: two steps either side of now, and no further.</summary>
    [Fact]
    public async Task ACodeIsAcceptedFromTwoStepsEitherSideOfNowAndNoFurther()
    {
        await using LocalSchemeRig rig = await CreateRigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);

        // In this order so that no refusal can be the spent step's doing: each one tried is later than
        // every step accepted before it.
        (await PresentAsync(rig, user, CodeAt(secret, -3), stepUp: false)).Succeeded.Should().BeFalse("three steps back is outside the window");
        (await PresentAsync(rig, user, CodeAt(secret, -2), stepUp: false)).Succeeded.Should().BeTrue("two steps back is inside it");
        (await PresentAsync(rig, user, CodeAt(secret, 3), stepUp: false)).Succeeded.Should().BeFalse("three steps ahead is outside the window");
        (await PresentAsync(rig, user, CodeAt(secret, 2), stepUp: false)).Succeeded.Should().BeTrue("two steps ahead is inside it");
    }

    [Fact]
    public async Task ACodePresentedAgainCountsTowardTheLockout()
    {
        await using LocalSchemeRig rig = await CreateRigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] secret = await rig.EnableAuthenticatorAsync(user);
        string code = CodeAt(secret, 0);

        await PresentAsync(rig, user, code, stepUp: false);
        await PresentAsync(rig, user, code, stepUp: false);

        int count = await rig.Context.Users.AsNoTracking()
                             .Where(u => u.Id == user.Id)
                             .Select(u => u.AccessFailedCount)
                             .SingleAsync(TestContext.Current.CancellationToken);
        count.Should().Be(1, "a spent code is a wrong answer, and costs what one costs");
    }

    [Fact]
    public async Task ANewKeyStartsWithNoStepSpent()
    {
        await using LocalSchemeRig rig = await CreateRigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        byte[] first = await rig.EnableAuthenticatorAsync(user);
        (await PresentAsync(rig, user, CodeAt(first, 0), stepUp: false)).Succeeded.Should().BeTrue();

        byte[] second = await rig.EnableAuthenticatorAsync(user);

        (await StoredStepAsync(rig, user)).Should().BeNull();
        AuthenticateResult result = await PresentAsync(rig, user, CodeAt(second, 0), stepUp: false);
        result.Succeeded.Should().BeTrue("the new key's first code, in the step the old key's code was spent in: {0}", result.Failure?.Message);
    }

    [Fact]
    public async Task ACodeIsNotSpentInsideATransaction()
    {
        await using LocalSchemeRig rig = await CreateRigAsync();
        HSUser user = await rig.AddUserAsync("owner@example.com");
        HSUserManager users = (HSUserManager)rig.Users;
        await using IDbContextTransaction transaction = await rig.Context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        Func<Task> spend = () => users.SpendAuthenticatorStepAsync(user, Now);

        await spend.Should().ThrowAsync<InvalidOperationException>("a rollback would make the code usable again");
    }
}
