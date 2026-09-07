using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The proof the Manage pages demand before an act a live session alone may not perform.
/// </summary>
/// <remarks>
/// <b>The case worth writing first is the one that fails open.</b> An account with no password has no
/// password to check, and the wrong answer to that - treating "nothing to prove" as "proved" - is a
/// hole that no test of the password path would ever notice.
/// </remarks>
public sealed class StepUpGateTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-stepup-{Guid.NewGuid():N}.db");

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

    private static StepUpGate GateOf(DefaultHttpContext request)
    {
        return request.RequestServices.GetRequiredService<StepUpGate>();
    }

    [Fact]
    public async Task TheRightPasswordProvesTheSignedInAccount()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        HSUser user = await rig.AddUserAsync("owner@homespool.example.net");
        DefaultHttpContext request = rig.NewRequest(await rig.SessionCookieAsync(user));

        // Act
        StepUpResult result = await GateOf(request).ProveAsync(request, user, LocalSchemeRig.Password);

        // Assert
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AWrongPasswordIsRefusedWithoutTouchingTheAccountLockout()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        HSUser user = await rig.AddUserAsync("owner@homespool.example.net");

        // Act
        DefaultHttpContext wrong = rig.NewRequest(await rig.SessionCookieAsync(user));
        StepUpResult refused = await GateOf(wrong).ProveAsync(wrong, user, "not the password");

        // Assert
        refused.Refusal.Should().Be(StepUpRefusal.WrongPassword);
        (await rig.Users.GetAccessFailedCountAsync(user))
            .Should().Be(0, "a wrong step-up backs off its own counter - locking the owner out is what the "
                            + "session holder would want");
    }

    [Fact]
    public async Task EnoughWrongPasswordsBackOffAndTheWaitReachesTheCaller()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        HSUser user = await rig.AddUserAsync("owner@homespool.example.net");
        int allowed = new AttemptLimitOptions().MaxFailedAttempts;

        // Act - the allowance and one past it, since the backoff starts on the failure that exceeds
        // it, then a request with the RIGHT password.
        for (int attempt = 0; attempt <= allowed; attempt += 1)
        {
            DefaultHttpContext wrong = rig.NewRequest(await rig.SessionCookieAsync(user));

            await GateOf(wrong).ProveAsync(wrong, user, "not the password");
        }

        DefaultHttpContext right = rig.NewRequest(await rig.SessionCookieAsync(user));
        StepUpResult refused = await GateOf(right).ProveAsync(right, user, LocalSchemeRig.Password);

        // Assert
        refused.Refusal.Should().Be(StepUpRefusal.LockedOut,
                                    "a backed-off step-up is refused before the password is compared");
        refused.RetryAfter.Should().NotBeNull().And.BeGreaterThan(TimeSpan.Zero,
                                                                 "the page says how long, and the account lockout "
                                                                 + "would answer zero here");
        (await rig.Users.IsLockedOutAsync(user))
            .Should().BeFalse("the account itself is untouched, so its owner can still sign in and take the "
                              + "session back");
    }

    [Fact]
    public async Task AnAccountWithNoPasswordIsRefusedRatherThanLetThrough()
    {
        // Arrange - an account created through a provider, which holds no password at all.
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        HSUser user = await rig.AddUserAsync("federated@homespool.example.net");
        (await rig.Users.RemovePasswordAsync(user)).Succeeded.Should().BeTrue();

        DefaultHttpContext request = rig.NewRequest(await rig.SessionCookieAsync(user));

        // Act - nothing to check, and no provider round trip has been taken.
        StepUpResult result = await GateOf(request).ProveAsync(request, user, password: null);

        // Assert
        result.Succeeded.Should().BeFalse("no password to check is not the same as a proved account");
        result.Refusal.Should().Be(StepUpRefusal.NoProviderProof);
    }

    [Fact]
    public async Task APasswordIsNotAcceptedFromAnAccountThatHasNone()
    {
        // Arrange
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        HSUser user = await rig.AddUserAsync("federated@homespool.example.net");
        (await rig.Users.RemovePasswordAsync(user)).Succeeded.Should().BeTrue();

        DefaultHttpContext request = rig.NewRequest(await rig.SessionCookieAsync(user));

        // Act - the password the account used to have, posted at the form anyway.
        StepUpResult result = await GateOf(request).ProveAsync(request, user, LocalSchemeRig.Password);

        // Assert
        result.Refusal.Should().Be(StepUpRefusal.NoProviderProof,
                                   "the branch is chosen by what the account holds, not by what was posted");
    }
}
