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
/// Proving, on a page the session has already reached, that the person at the keyboard holds the
/// account: the password where there is one, and a fresh round trip to the account's own identity
/// provider where there is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>A session is not a proof of anything but continuity.</b> It says a browser signed in once; the
/// acts these pages perform - clearing the second factor, minting recovery codes, moving the address
/// a password reset goes to - are the acts a walk-up on an unlocked browser wants, and each of them
/// outlives the session that did it. So the credential is asked for again at the act, and the act is
/// what a stolen cookie can no longer perform.
/// </para>
/// <para>
/// <b>Not the authenticator code, on the pages that can re-key it.</b> The code proves possession of
/// the device, and the page that resets the key exists for the person whose device is gone -
/// demanding a code there would shut the door on the only people who need it. The password is the
/// credential that survives a lost phone, which is why it is the one asked for.
/// </para>
/// <para>
/// <b>And not a recovery code</b>, though it is tempting on exactly these pages:
/// <see cref="Schemes.RecoveryCode"/> is for getting back into an account rather than for confirming
/// an act inside one, and spending one here would let a code minted for the locked-out case be the
/// key to minting ten more.
/// </para>
/// <para>
/// <b>An account created through a provider has no password by rule</b>, so for it the proof is a
/// fresh authentication at that provider - <c>prompt=login</c> and <c>max_age=0</c>, bound to a login
/// this account actually holds, and spent once. That is the same shape <c>Manage/Passkeys</c> uses,
/// and it is here rather than there so the four pages cannot drift in what they accept.
/// </para>
/// <para>
/// <b>The proof is carried by <see cref="PasskeyCeremonies"/>, whose cookie is scoped to the page
/// that issued it.</b> A proof earned on one page is therefore not spendable on another: each page
/// sends the person to the provider itself and reads the answer back on its own path. That is a
/// property of the cookie rather than a check here, and it is the reason no page has to name which
/// act a proof was for.
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
    private readonly ExternalSignIn _externalSignIn;
    private readonly PasskeyCeremonies _ceremonies;
    private readonly TimeProvider _time;
    private readonly ILogger<StepUpGate> _logger;

    public StepUpGate(UserManager<HSUser> users,
                      ExternalSignIn externalSignIn,
                      PasskeyCeremonies ceremonies,
                      TimeProvider time,
                      ILogger<StepUpGate> logger)
    {
        _users = users;
        _externalSignIn = externalSignIn;
        _ceremonies = ceremonies;
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
    /// Proves <paramref name="user"/> by whichever credential it has: the password when there is one,
    /// the provider proof when there is not. <b>The one entry point the pages call</b>, so which
    /// credential answers for which account is decided here rather than four times over.
    /// </summary>
    public async Task<StepUpResult> ProveAsync(HttpContext context, HSUser user, string? password)
    {
        return await UsesPasswordAsync(user)
            ? await PasswordAsync(context, password)
            : await ProviderProofAsync(context, user);
    }

    /// <summary>
    /// Checks <paramref name="password"/> against the signed-in account through
    /// <see cref="Schemes.UserPassword"/>, which counts a wrong one toward the lockout and refuses a
    /// locked-out account before comparing anything.
    /// </summary>
    public async Task<StepUpResult> PasswordAsync(HttpContext context, string? password)
    {
        ArgumentNullException.ThrowIfNull(context);

        AuthenticateResult stepUp = await context.AuthenticateWithAsync(Schemes.UserPassword, new PasswordCredential(password));

        if (stepUp.Succeeded)
        {
            return StepUpResult.Proved;
        }

        return stepUp.Refusal() == SignInRefusal.LockedOut
            ? StepUpResult.Refused(StepUpRefusal.LockedOut)
            : StepUpResult.Refused(StepUpRefusal.WrongPassword);
    }

    /// <summary>
    /// Spends the provider proof this request carries, for an account with no password: it must have
    /// been earned on this page, be unexpired, name a login <paramref name="user"/> holds, and not
    /// have been spent already.
    /// </summary>
    public async Task<StepUpResult> ProviderProofAsync(HttpContext context, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        PasskeyCeremonies.Outcome proof = _ceremonies.Take(context, PasskeyCeremonies.ProviderProof);

        if (!proof.Succeeded)
        {
            _logger.LogInformation("Step-up refused for user {UserId}: {Reason}.", user.Id, proof.Reason);

            return StepUpResult.Refused(StepUpRefusal.NoProviderProof);
        }

        // The cookie is bound to the browser and not to the account, so a proof earned for one account
        // and presented with another account's session is not that account's proof.
        IList<UserLoginInfo> logins = await _users.GetLoginsAsync(user);

        if (logins.All(login => !string.Equals(login.ProviderKey, proof.EngineState, StringComparison.Ordinal)))
        {
            _logger.LogWarning("Step-up refused for user {UserId}: the provider proof was for another account.", user.Id);

            return StepUpResult.Refused(StepUpRefusal.NoProviderProof);
        }

        if (_ceremonies.Spend(proof) is { } notSpent)
        {
            _logger.LogInformation("Step-up refused for user {UserId}: {Reason}.", user.Id, notSpent);

            return StepUpResult.Refused(StepUpRefusal.NoProviderProof);
        }

        return StepUpResult.Proved;
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

        if (await _users.HasPasswordAsync(user)
            || (await _users.GetLoginsAsync(user)).All(login => !string.Equals(login.LoginProvider, provider, StringComparison.Ordinal)))
        {
            return null;
        }

        AuthenticationProperties external = ExternalSignIn.ChallengeProperties(
            provider, redirectUrl, user.Id.ToString(CultureInfo.InvariantCulture));

        return new OpenIdConnectChallengeProperties(external.Items, external.Parameters)
        {
            MaxAge = TimeSpan.Zero,
            Prompt = "login",
        };
    }

    /// <summary>
    /// Reads the provider's answer on the way back and, when it counts, starts the proof for the act
    /// on this page to spend. The outcome names the provider, for the sentence the page shows, and why
    /// there is no proof when there is none.
    /// </summary>
    public async Task<ProviderProofOutcome> RecordProviderProofAsync(HttpContext context, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        // Keyed on the signed-in account, as the account-linking callback is, so a callback carrying
        // somebody else's external cookie is not read as this account's.
        ExternalLoginInfo? info = await _externalSignIn.InfoAsync(context, user.Id.ToString(CultureInfo.InvariantCulture));

        // Consumed either way: a provider identity is not left lying around for another page to find.
        await context.SignOutAsync(IdentityConstants.ExternalScheme);

        if (info is null)
        {
            return new ProviderProofOutcome("failed", null);
        }

        string provider = info.ProviderDisplayName ?? info.LoginProvider;

        if (ProviderProofRefusal(info, await _users.GetLoginsAsync(user), _time.GetUtcNow()) is { } refusal)
        {
            _logger.LogWarning("Provider re-authentication refused for user {UserId} via {LoginProvider}: {Reason}.",
                               user.Id,
                               info.LoginProvider,
                               refusal);

            return new ProviderProofOutcome(refusal, provider);
        }

        _ceremonies.Begin(context, PasskeyCeremonies.ProviderProof, info.ProviderKey);

        _logger.LogInformation("User {UserId} re-authenticated at {LoginProvider}.", user.Id, info.LoginProvider);

        return new ProviderProofOutcome(null, provider);
    }

    /// <summary>
    /// Why a provider's answer does not count as this account re-authenticating, or
    /// <see langword="null"/> when it does: <c>"mismatch"</c> when the subject is not one this account
    /// signs in with, <c>"stale"</c> when the provider reports a sign-in older than
    /// <see cref="MaxProviderProofAge"/>. A provider that reports no sign-in time is taken at its word.
    /// </summary>
    public static string? ProviderProofRefusal(ExternalLoginInfo info, IEnumerable<UserLoginInfo> logins, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(logins);

        bool held = logins.Any(login => string.Equals(login.LoginProvider, info.LoginProvider, StringComparison.Ordinal)
                                        && string.Equals(login.ProviderKey, info.ProviderKey, StringComparison.Ordinal));

        if (!held)
        {
            return "mismatch";
        }

        string? authTime = info.Principal.FindFirstValue(JwtClaimTypes.AuthenticationTime);

        if (authTime is not null
            && long.TryParse(authTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            && now - DateTimeOffset.FromUnixTimeSeconds(seconds) > MaxProviderProofAge)
        {
            return "stale";
        }

        return null;
    }
}
