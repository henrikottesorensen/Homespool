using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Redeems a recovery code for the account the pending two-factor cookie names, and returns that
/// account's principal. Only ever a second factor: a recovery code is for getting back into an
/// account, and the pending cookie is the only place this scheme looks for one.
/// </summary>
/// <remarks>
/// <c>SignInManager.TwoFactorRecoveryCodeSignInAsync</c> at v10.0.11 is the transcription source. As
/// there, a wrong code is not counted against the lockout: the codes are random and long, and the
/// framework's reasoning that brute force is not the risk holds. A redeemed code is spent by the
/// store, so the same one cannot answer twice.
/// </remarks>
public sealed class RecoveryCodeAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The <see cref="ClaimTypes.AuthenticationMethod"/> the principal carries: the framework's word for a second factor.</summary>
    public const string AuthenticationMethod = "mfa";

    private readonly UserManager<HSUser> _users;
    private readonly IUserClaimsPrincipalFactory<HSUser> _claimsFactory;
    private readonly LocalSignInRules _rules;

    public RecoveryCodeAuthenticationHandler(UserManager<HSUser> users,
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
        string? code = Normalise(Context.Features.Get<RecoveryCodeCredential>()?.Code);

        if (code is null)
        {
            return AuthenticateResult.NoResult();
        }

        HSUser? user = await _rules.PendingTwoFactorAccountAsync(Context);

        if (user is null)
        {
            Logger.LogInformation("Recovery code refused: no account is pending a second factor.");

            return SignInRefusals.Fail(SignInRefusal.Invalid, "There is no account to redeem a code for.");
        }

        if (await _rules.PreSignInCheckAsync(user) is { } refusal)
        {
            Logger.LogInformation("Recovery code refused for user {UserId}: {Refusal}.", user.Id, refusal);

            return SignInRefusals.Fail(refusal, "The account may not sign in.");
        }

        IdentityResult redeemed = await _users.RedeemTwoFactorRecoveryCodeAsync(user, code);

        if (!redeemed.Succeeded)
        {
            Logger.LogInformation("Recovery code refused for user {UserId}: not one of the account's unspent codes.", user.Id);

            return SignInRefusals.Fail(SignInRefusal.Invalid, "Invalid recovery code.");
        }

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        Logger.LogInformation("Recovery code redeemed for user {UserId}.", user.Id);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>The code without the spaces the page shows it with, or <see langword="null"/> when none was presented.</summary>
    private static string? Normalise(string? code)
    {
        if (code is null)
        {
            return null;
        }

        string bare = code.Replace(" ", string.Empty, StringComparison.Ordinal);

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
