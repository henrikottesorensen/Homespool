using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// The credentials <c>Account/Reauthenticate</c> proves an account by, other than a passkey: the
/// password where there is one, and a fresh round trip to the account's own identity provider where
/// there is not. The page composes; this verifies.
/// </summary>
/// <remarks>
/// <para>
/// <b>A session is not a proof of anything but continuity.</b> It says a browser signed in once; the
/// acts behind <see cref="RequireRecentProofAttribute"/> - clearing the second factor, minting
/// recovery codes, moving the address a password reset goes to, closing somebody's account - are the
/// acts a walk-up on an unlocked browser wants, and each of them outlives the session that did it. So
/// a credential is asked for again, on one page, and <see cref="RecentProof"/> remembers that it was.
/// </para>
/// <para>
/// <b>Not the authenticator code.</b> The code proves possession of the device, and the page that
/// resets the key exists for the person whose device is gone - demanding a code would shut the door on
/// the only people who need it. The password, a passkey or the provider are what survive a lost phone.
/// <b>And not a recovery code</b>: <see cref="Schemes.RecoveryCode"/> is for getting back into an
/// account rather than for confirming an act inside one.
/// </para>
/// <para>
/// <b>An account created through a provider has no password by rule</b>, so for it the proof is a
/// fresh authentication at that provider - <c>prompt=login</c> and <c>max_age=0</c>, bound to a login
/// this account actually holds - and <see cref="ProviderProofRefusal"/> checks the answer rather than
/// trusting the request, because a provider is free to ignore both.
/// </para>
/// </remarks>
public sealed class StepUpGate
{
    /// <summary>
    /// How stale a provider's reported sign-in may be and still count as re-authentication. Two
    /// minutes covers a round trip a person actually completed; a provider that answers instantly
    /// from an existing session reports a time far older than this and is refused, which is the whole
    /// point of asking.
    /// </summary>
    public static readonly TimeSpan MaxProviderProofAge = TimeSpan.FromMinutes(2);

    private readonly UserManager<HSUser> _users;
    private readonly LocalSignInRules _rules;
    private readonly ExternalSignIn _externalSignIn;
    private readonly TimeProvider _time;
    private readonly ILogger<StepUpGate> _logger;

    public StepUpGate(UserManager<HSUser> users,
                      LocalSignInRules rules,
                      ExternalSignIn externalSignIn,
                      TimeProvider time,
                      ILogger<StepUpGate> logger)
    {
        _users = users;
        _rules = rules;
        _externalSignIn = externalSignIn;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="user"/> proves itself with a password. False means the provider round
    /// trip is the only proof available, and the page asks for no password because there is none.
    /// </summary>
    public Task<bool> UsesPasswordAsync(HSUser user)
    {
        return _users.HasPasswordAsync(user);
    }

    /// <summary>
    /// Checks <paramref name="password"/> against the signed-in account through
    /// <see cref="Schemes.UserPassword"/>, which refuses an account that may not sign in before
    /// comparing anything.
    /// </summary>
    /// <remarks>
    /// <b>What a wrong one costs is the scheme's to decide, not this class's.</b> It backs off the
    /// account's step-up counter and leaves the account lockout alone - a session holder guessing here
    /// must not be able to lock the owner out of taking the session back. The refusal carries how long
    /// the backoff lasts, which is the only part of it a page can act on.
    /// </remarks>
    public async Task<StepUpResult> PasswordAsync(HttpContext context, string? password)
    {
        ArgumentNullException.ThrowIfNull(context);

        AuthenticateResult stepUp = await context.AuthenticateWithAsync(Schemes.UserPassword, new PasswordCredential(password));

        if (stepUp.Succeeded)
        {
            return StepUpResult.Proved;
        }

        return stepUp.Refusal() == SignInRefusal.LockedOut ?
            StepUpResult.Refused(StepUpRefusal.LockedOut, stepUp.RetryAfter()) :
            StepUpResult.Refused(StepUpRefusal.WrongPassword);
    }

    /// <summary>
    /// What sends a password-less account to <paramref name="provider"/> to re-authenticate, coming
    /// back to <paramref name="redirectUrl"/>. <see langword="null"/> when this account does not sign
    /// in with that provider, which the caller answers as a 404 rather than as a refusal.
    /// </summary>
    /// <remarks>
    /// <c>MaxAge</c> zero and <c>prompt=login</c> together are what make this a re-authentication
    /// rather than a redirect that returns instantly from the provider's own session - and
    /// <see cref="ProviderProofRefusal"/> checks the answer rather than trusting the request, because
    /// a provider is free to ignore both.
    /// </remarks>
    public async Task<AuthenticationProperties?> ProviderChallengeAsync(HSUser user, string provider, string redirectUrl)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (await _users.HasPasswordAsync(user) ||
            (await _users.GetLoginsAsync(user)).All(login => !string.Equals(login.LoginProvider, provider, StringComparison.Ordinal)))
        {
            return null;
        }

        AuthenticationProperties external = ExternalSignIn.ChallengeProperties(
            provider, redirectUrl, ExternalRoundTrip.Reauthenticate, user.Id.ToString(CultureInfo.InvariantCulture));

        return new OpenIdConnectChallengeProperties(external.Items, external.Parameters)
        {
            MaxAge = TimeSpan.Zero,
            Prompt = "login",
        };
    }

    /// <summary>
    /// Reads the provider's answer on the way back - consuming the external cookie either way - and
    /// says whether it counts as <paramref name="user"/> re-authenticating: the account must be one
    /// that may sign in, the subject must be one it signs in with, the answer must be read soon after
    /// it came back, and any sign-in time the provider reports must be recent.
    /// </summary>
    /// <remarks>
    /// <b>The account's standing is asked here because no scheme asks it on this path.</b> The
    /// password, authenticator and passkey step-ups each go through a scheme that refuses an account
    /// that may not sign in before comparing anything; a provider's answer is read directly, so
    /// without this a closed account holding a session could still earn a proof.
    /// </remarks>
    public async Task<ProviderProofOutcome> VerifyProviderProofAsync(HttpContext context, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        // Keyed on the signed-in account, as the account-linking callback is, so a callback carrying
        // somebody else's external cookie is not read as this account's.
        ExternalLoginInfo? info = await _externalSignIn.InfoAsync(context, ExternalRoundTrip.Reauthenticate, user.Id.ToString(CultureInfo.InvariantCulture));

        // Consumed either way: a provider identity is not left lying around for another page to find.
        await context.SignOutAsync(IdentityConstants.ExternalScheme);

        if (info is null)
        {
            return new ProviderProofOutcome("failed", null);
        }

        string provider = info.ProviderDisplayName ?? info.LoginProvider;

        if (await _rules.StandingCheckAsync(user) is not null)
        {
            _logger.LogWarning("Provider re-authentication refused for user {UserId} via {LoginProvider}: the account may not sign in.",
                               user.Id,
                               info.LoginProvider);

            return new ProviderProofOutcome("failed", provider);
        }

        if (ProviderProofRefusal(info, await _users.GetLoginsAsync(user), _time.GetUtcNow()) is { } refusal)
        {
            _logger.LogWarning("Provider re-authentication refused for user {UserId} via {LoginProvider}: {Reason}.",
                               user.Id,
                               info.LoginProvider,
                               refusal);

            return new ProviderProofOutcome(refusal, provider);
        }

        _logger.LogInformation("User {UserId} re-authenticated at {LoginProvider}.", user.Id, info.LoginProvider);

        return new ProviderProofOutcome(null, provider);
    }

    /// <summary>
    /// Why a provider's answer does not count as this account re-authenticating, or
    /// <see langword="null"/> when it does: <c>"mismatch"</c> when the subject is not one this account
    /// signs in with; <c>"failed"</c> when the answer carries no <c>auth_time</c>, which this server
    /// writes on every answer that came through the provider handler; <c>"stale"</c> when that
    /// <c>auth_time</c>, or the sign-in the provider reports in
    /// <see cref="HSClaimTypes.ExternalAuthenticationTime"/>, is older than
    /// <see cref="MaxProviderProofAge"/>.
    /// </summary>
    /// <remarks>
    /// <b>A provider that reports no sign-in time of its own is taken at its word</b>, and so is one
    /// whose report is not a time. This server's <c>auth_time</c> bounds how long its answer may wait to
    /// be read; only the provider's time can show that it answered from a session it already had rather
    /// than asking again, and refusing every provider that does not say would refuse accounts that have
    /// no other way to prove themselves.
    /// </remarks>
    public static string? ProviderProofRefusal(ExternalLoginInfo info, IEnumerable<UserLoginInfo> logins, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(logins);

        bool held = logins.Any(login => string.Equals(login.LoginProvider, info.LoginProvider, StringComparison.Ordinal) &&
                                        string.Equals(login.ProviderKey, info.ProviderKey, StringComparison.Ordinal));

        if (!held)
        {
            return "mismatch";
        }

        if (UnixTime(info.Principal, JwtClaimTypes.AuthenticationTime) is not { } answered)
        {
            return "failed";
        }

        if (now - answered > MaxProviderProofAge ||
            (UnixTime(info.Principal, HSClaimTypes.ExternalAuthenticationTime) is { } reported && now - reported > MaxProviderProofAge))
        {
            return "stale";
        }

        return null;
    }

    /// <summary>
    /// The instant a claim of <paramref name="claimType"/> names in Unix seconds, or
    /// <see langword="null"/> when there is none or it is not one - including a number outside what
    /// <see cref="DateTimeOffset"/> can hold, which a provider is free to send.
    /// </summary>
    private static DateTimeOffset? UnixTime(ClaimsPrincipal principal, string claimType)
    {
        return long.TryParse(principal.FindFirstValue(claimType), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) &&
               seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
               seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ?
            DateTimeOffset.FromUnixTimeSeconds(seconds) :
            null;
    }
}
