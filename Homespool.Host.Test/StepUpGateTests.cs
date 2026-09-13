using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using AwesomeAssertions;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Accounts;
using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The password half of the proof page's engine, and what it makes of a provider's answer.
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
        StepUpResult result = await GateOf(request).PasswordAsync(request, LocalSchemeRig.Password);

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
        StepUpResult refused = await GateOf(wrong).PasswordAsync(wrong, "not the password");

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

            await GateOf(wrong).PasswordAsync(wrong, "not the password");
        }

        DefaultHttpContext right = rig.NewRequest(await rig.SessionCookieAsync(user));
        StepUpResult refused = await GateOf(right).PasswordAsync(right, LocalSchemeRig.Password);

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

    /// <summary>
    /// An account with no password is refused whatever is posted: the scheme has nothing to compare
    /// and says so as a wrong password, never as a proof.
    /// </summary>
    [Fact]
    public async Task AnAccountWithNoPasswordIsRefusedRatherThanLetThrough()
    {
        // Arrange - an account created through a provider, which holds no password at all.
        await using LocalSchemeRig rig = await LocalSchemeRig.CreateAsync(_databasePath);

        HSUser user = await rig.AddUserAsync("federated@homespool.example.net");
        (await rig.Users.RemovePasswordAsync(user)).Succeeded.Should().BeTrue();

        DefaultHttpContext nothing = rig.NewRequest(await rig.SessionCookieAsync(user));
        DefaultHttpContext theOldOne = rig.NewRequest(await rig.SessionCookieAsync(user));

        // Act - nothing, and the password the account used to have.
        StepUpResult empty = await GateOf(nothing).PasswordAsync(nothing, password: null);
        StepUpResult stale = await GateOf(theOldOne).PasswordAsync(theOldOne, LocalSchemeRig.Password);

        // Assert
        empty.Succeeded.Should().BeFalse("no password to check is not the same as a proved account");
        stale.Succeeded.Should().BeFalse("the password the account no longer has proves nothing");
        (await GateOf(nothing).UsesPasswordAsync(user)).Should().BeFalse("the page asks this before offering the field");
    }

    [Fact]
    public void AProvidersAnswerCountsOnlyForTheSubjectTheAccountSignsInWith()
    {
        DateTimeOffset now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        UserLoginInfo[] logins = [new(Schemes.ExternalOidc, "subject-1", "Dex")];

        StepUpGate.ProviderProofRefusal(Answer("subject-1", authTime: null), logins, now)
                     .Should().BeNull("the subject matches and the provider reported no sign-in time, so it is taken at its word");
        StepUpGate.ProviderProofRefusal(Answer("subject-2", authTime: null), logins, now)
                     .Should().Be("mismatch", "another account at the same provider is not this account re-authenticating");
        StepUpGate.ProviderProofRefusal(Answer("subject-1", authTime: now.AddSeconds(-30)), logins, now)
                     .Should().BeNull("a sign-in half a minute ago is what max_age=0 asked for");
        StepUpGate.ProviderProofRefusal(Answer("subject-1", authTime: now.AddMinutes(-10)), logins, now)
                     .Should().Be("stale", "the provider reused a session it already had instead of asking again");
    }

    private static ExternalLoginInfo Answer(string subject, DateTimeOffset? authTime)
    {
        List<Claim> claims = [new(JwtClaimTypes.Subject, subject)];

        if (authTime is { } time)
        {
            claims.Add(new Claim(JwtClaimTypes.AuthenticationTime, time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        return new ExternalLoginInfo(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), Schemes.ExternalOidc, subject, "Dex");
    }
}
