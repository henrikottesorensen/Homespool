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
/// <b>Whose code, in this order.</b> The pending two-factor cookie, which exists only because the
/// password scheme just succeeded and the account owes a second factor; otherwise the signed-in
/// session, for a step-up on a page the person already reached. Neither present, the code is refused
/// unread. The ticket says which it was, under <see cref="SourceProperty"/>, so the page that
/// composed the two factors knows whether it is completing a sign-in or confirming an act.
/// </para>
/// <para>
/// <b>The framework's authenticator check, as a scheme.</b>
/// <c>SignInManager.TwoFactorAuthenticatorSignInAsync</c> at v10.0.11 is the transcription source:
/// the pre-sign-in check, the code through the authenticator token provider, a wrong one counted
/// against the lockout, a right one resetting it. What is left behind is the sign-in itself, the
/// remembered-machine cookie and the clearing of the pending cookie - the page composes those.
/// </para>
/// </remarks>
public sealed class TotpAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The <see cref="JwtClaimTypes.AuthenticationMethod"/> a code-authenticated principal carries: the framework's own word.</summary>
    public const string AuthenticationMethod = "mfa";

    /// <summary>The ticket property naming where the account came from: <see cref="PendingSource"/> or <see cref="SessionSource"/>.</summary>
    public const string SourceProperty = "Homespool.Totp.Source";

    /// <summary>The account was the one owing a second factor after a password.</summary>
    public const string PendingSource = "pending";

    /// <summary>The account was the signed-in session's.</summary>
    public const string SessionSource = "session";

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
        string? code = Normalise(Context.Features.Get<TotpCredential>()?.Code);

        if (code is null)
        {
            return AuthenticateResult.NoResult();
        }

        (HSUser? user, string source) = await AccountAsync();

        if (user is null)
        {
            Logger.LogInformation("Authenticator code refused: no account is pending a second factor and nobody is signed in.");

            return SignInRefusals.Fail(SignInRefusal.Invalid, "There is no account to verify a code for.");
        }

        if (await _rules.PreSignInCheckAsync(user) is { } refusal)
        {
            Logger.LogInformation("Authenticator code refused for user {UserId}: {Refusal}.", user.Id, refusal);

            return SignInRefusals.Fail(refusal, "The account may not sign in.");
        }

        if (!await _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            bool lockedOut = await _rules.RecordFailureAsync(user);

            Logger.LogInformation("Authenticator code refused for user {UserId}: wrong code{LockedOut}.",
                                  user.Id,
                                  lockedOut ? ", now locked out" : string.Empty);

            return SignInRefusals.Fail(lockedOut ? SignInRefusal.LockedOut : SignInRefusal.Invalid, "Invalid authenticator code.");
        }

        IdentityResult reset = await _users.ResetAccessFailedCountAsync(user);

        if (!reset.Succeeded)
        {
            Logger.LogWarning("Authenticator code refused for user {UserId}: the failed count could not be reset.", user.Id);

            return SignInRefusals.Fail(SignInRefusal.Invalid, "Invalid authenticator code.");
        }

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        AuthenticationProperties properties = new();
        properties.Items[SourceProperty] = source;

        Logger.LogInformation("Authenticator code verified for user {UserId} ({Source}).", user.Id, source);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, properties, Scheme.Name));
    }

    private async Task<(HSUser? user, string source)> AccountAsync()
    {
        HSUser? pending = await _rules.PendingTwoFactorAccountAsync(Context);

        if (pending is not null)
        {
            return (pending, PendingSource);
        }

        return (await _rules.SignedInAccountAsync(Context), SessionSource);
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
