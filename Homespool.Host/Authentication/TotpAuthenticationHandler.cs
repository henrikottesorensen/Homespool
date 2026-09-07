using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Verifies an authenticator code for an account some other scheme has already named, and returns
/// that account's principal. A code alone identifies nobody, which is what keeps this scheme from
/// ever being a sign-in on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whose code, said by the credential.</b> A <see cref="TotpCredential"/> completes a sign-in and
/// is verified against the account the pending two-factor cookie names, which exists only because
/// the password scheme just succeeded and the account owes a second factor. A
/// <see cref="TotpStepUpCredential"/> confirms an act and is verified against the signed-in session's
/// account. Neither falls back to the other: a browser can hold a session for one account and a
/// pending sign-in for another at once, and a step-up that took the pending account's code would let
/// whoever holds that account pass the signed-in account's check. The account a credential names
/// being absent, the code is refused unread; both credentials presented together are refused.
/// </para>
/// <para>
/// <b>The framework's authenticator check, as a scheme.</b>
/// <c>SignInManager.TwoFactorAuthenticatorSignInAsync</c> at v10.0.11 is the transcription source:
/// the pre-sign-in check, the code through the authenticator token provider, a wrong one counted
/// against the lockout, a right one resetting it. What is left behind is the sign-in itself, the
/// remembered-machine cookie and the clearing of the pending cookie - the page composes those.
/// <b>A step-up counts differently</b>: a wrong code backs off the account's step-ups through
/// <see cref="LocalSignInRules.StepUpBackoffAsync"/> and never touches the account lockout, since a
/// session holder guessing at a step-up must not be able to lock the owner out of signing in.
/// </para>
/// </remarks>
public sealed class TotpAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The <see cref="JwtClaimTypes.AuthenticationMethod"/> a code-authenticated principal carries: the framework's own word.</summary>
    public const string AuthenticationMethod = "mfa";

    private readonly UserManager<HSUser> _users;
    private readonly IUserClaimsPrincipalFactory<HSUser> _claimsFactory;
    private readonly LocalSignInRules _rules;

    public TotpAuthenticationHandler(UserManager<HSUser> users,
                                     IUserClaimsPrincipalFactory<HSUser> claimsFactory,
                                     LocalSignInRules rules,
                                     IOptionsMonitor<AuthenticationSchemeOptions> options,
                                     ILoggerFactory loggerFactory,
                                     UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
        _users = users;
        _claimsFactory = claimsFactory;
        _rules = rules;
    }

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        TotpCredential? signIn = Context.Features.Get<TotpCredential>();
        TotpStepUpCredential? stepUp = Context.Features.Get<TotpStepUpCredential>();

        if (signIn is null && stepUp is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (signIn is not null && stepUp is not null)
        {
            return SignInRefusals.Fail(SignInRefusal.Invalid, "A sign-in code and a step-up code cannot be presented together.");
        }

        string? code = Normalise(signIn?.Code ?? stepUp?.Code);

        if (code is null)
        {
            return SignInRefusals.Fail(SignInRefusal.Invalid, "A code is required.");
        }

        HSUser? user = signIn is not null
            ? await _rules.PendingTwoFactorAccountAsync(Context)
            : await _rules.SignedInAccountAsync(Context);

        if (user is null)
        {
            Logger.LogInformation("Authenticator code refused: {Absent}.",
                                  signIn is not null ? "no account is pending a second factor" : "nobody is signed in");

            return SignInRefusals.Fail(SignInRefusal.Invalid, "There is no account to verify a code for.");
        }

        if (await _rules.PreSignInCheckAsync(user) is { } refusal)
        {
            Logger.LogInformation("Authenticator code refused for user {UserId}: {Refusal}.", user.Id, refusal);

            return SignInRefusals.Fail(refusal, "The account may not sign in.", await _rules.RemainingLockoutAsync(user));
        }

        // A step-up has its own backoff, checked before the code is compared and counted instead of
        // the account lockout: a session holder guessing here must not lock the owner out.
        if (stepUp is not null && await _rules.StepUpBackoffAsync(user, Context.RequestAborted) is { } backedOff)
        {
            Logger.LogInformation("Authenticator code refused for user {UserId}: step-ups backed off for {Remaining}.", user.Id, backedOff);

            return SignInRefusals.Fail(SignInRefusal.LockedOut, "Too many wrong step-ups.", backedOff);
        }

        if (!await _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            if (stepUp is not null)
            {
                await _rules.RecordStepUpFailureAsync(user, Context.RequestAborted);

                Logger.LogInformation("Authenticator code refused for user {UserId}: wrong step-up code.", user.Id);

                return SignInRefusals.Fail(SignInRefusal.Invalid, "Invalid authenticator code.");
            }

            bool lockedOut = await _rules.RecordFailureAsync(user);

            Logger.LogInformation("Authenticator code refused for user {UserId}: wrong code{LockedOut}.",
                                  user.Id,
                                  lockedOut ? ", now locked out" : string.Empty);

            return SignInRefusals.Fail(lockedOut ? SignInRefusal.LockedOut : SignInRefusal.Invalid, "Invalid authenticator code.");
        }

        if (stepUp is not null)
        {
            await _rules.ResetStepUpAsync(user, Context.RequestAborted);
        }
        else
        {
            IdentityResult reset = await _users.ResetAccessFailedCountAsync(user);

            if (!reset.Succeeded)
            {
                Logger.LogWarning("Authenticator code refused for user {UserId}: the failed count could not be reset.", user.Id);

                return SignInRefusals.Fail(SignInRefusal.Invalid, "Invalid authenticator code.");
            }
        }

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        Logger.LogInformation("Authenticator code verified for user {UserId} ({Purpose}).",
                              user.Id,
                              signIn is not null ? "completing a sign-in" : "a step-up");

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>
    /// The code with the spaces and dashes authenticator apps show removed, or <see langword="null"/>
    /// when none was presented.
    /// </summary>
    private static string? Normalise(string? code)
    {
        if (code is null)
        {
            return null;
        }

        string bare = code.Replace(" ", string.Empty, StringComparison.Ordinal)
                          .Replace("-", string.Empty, StringComparison.Ordinal);

        return bare.Length == 0 ? null : bare;
    }

    /// <inheritdoc/>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;

        return Task.CompletedTask;
    }
}
