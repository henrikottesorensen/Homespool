using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Turns a username or address and a password, posted in a form, into the account's principal.
/// Nothing more: whether that becomes a session, or the first half of one, is the page's decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>The framework's password check, as a scheme.</b> <c>SignInManager.CheckPasswordSignInAsync</c>
/// at v10.0.11 is the transcription source, step for step: the pre-sign-in check (may this account
/// sign in at all, is it locked out), the password compared through <see cref="UserManager{TUser}"/>,
/// a wrong one counted against the lockout, and one quirk kept deliberately - <b>a right password
/// does not reset the failed count while the account still owes a second factor</b>, or an attacker
/// holding the password would get the count reset before every run at the code. The code's own
/// scheme resets it when the code is right.
/// </para>
/// <para>
/// <b>The one scheme that names its own account.</b> The login field resolves by username and then by
/// address, in one field, which is safe because a username may not contain <c>@</c>. An identifier
/// nobody holds is refused after a decoy verification, so a miss costs what a wrong password costs
/// and the form is not an enumeration oracle in its timing any more than in its wording.
/// </para>
/// <para>
/// <b>Nothing here reads the form, signs anyone in, sets a cookie or reads one</b>, beyond the
/// remembered-machine cookie the quirk above consults. The page binds the form and presents a
/// <see cref="UserPasswordCredential"/> through <see cref="CredentialAuthentication"/>; a ticket
/// from this scheme is a proof, held by the page that asked for it.
/// </para>
/// </remarks>
public sealed class UserPasswordAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The <see cref="JwtClaimTypes.AuthenticationMethod"/> a password-authenticated principal carries: the framework's own word.</summary>
    public const string AuthenticationMethod = "pwd";

    private readonly UserManager<HSUser> _users;
    private readonly IUserClaimsPrincipalFactory<HSUser> _claimsFactory;
    private readonly IPasswordHasher<HSUser> _hasher;
    private readonly LocalSignInRules _rules;

    public UserPasswordAuthenticationHandler(UserManager<HSUser> users,
                                             IUserClaimsPrincipalFactory<HSUser> claimsFactory,
                                             IPasswordHasher<HSUser> hasher,
                                             LocalSignInRules rules,
                                             IOptionsMonitor<AuthenticationSchemeOptions> options,
                                             ILoggerFactory loggerFactory,
                                             UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
        _users = users;
        _claimsFactory = claimsFactory;
        _hasher = hasher;
        _rules = rules;
    }

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        UserPasswordCredential? credential = Context.Features.Get<UserPasswordCredential>();

        // No credential presented: this scheme has nothing to say about the request.
        if (credential is null)
        {
            return AuthenticateResult.NoResult();
        }

        (string? login, string? password) = credential;

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return SignInRefusals.Fail(SignInRefusal.Invalid, "A login and a password are both required.");
        }

        // Username first because it is the cheaper lookup; the order settles nothing, since the two
        // namespaces cannot overlap.
        HSUser? user = await _users.FindByNameAsync(login) ?? await _users.FindByEmailAsync(login);

        if (user is null)
        {
            PasswordVerificationDecoy.Verify(_hasher, password);

            Logger.LogInformation("Password sign-in refused: no account holds the identifier presented.");

            return SignInRefusals.Fail(SignInRefusal.Invalid, "Invalid login attempt.");
        }

        if (await _rules.PreSignInCheckAsync(user) is { } refusal)
        {
            Logger.LogInformation("Password sign-in refused for user {UserId}: {Refusal}.", user.Id, refusal);

            return SignInRefusals.Fail(refusal, "The account may not sign in.");
        }

        if (!await _users.CheckPasswordAsync(user, password))
        {
            bool lockedOut = await _rules.RecordFailureAsync(user);

            Logger.LogInformation("Password sign-in refused for user {UserId}: wrong password{LockedOut}.",
                                  user.Id,
                                  lockedOut ? ", now locked out" : string.Empty);

            return SignInRefusals.Fail(lockedOut ? SignInRefusal.LockedOut : SignInRefusal.Invalid, "Invalid login attempt.");
        }

        // The framework's quirk, kept: while a second factor is still owed, the failed count stands
        // until the code is right, unless this browser was remembered after one.
        if (!await _users.GetTwoFactorEnabledAsync(user) || await _rules.IsTwoFactorClientRememberedAsync(Context, user))
        {
            IdentityResult reset = await _users.ResetAccessFailedCountAsync(user);

            if (!reset.Succeeded)
            {
                Logger.LogWarning("Password sign-in refused for user {UserId}: the failed count could not be reset.", user.Id);

                return SignInRefusals.Fail(SignInRefusal.Invalid, "Invalid login attempt.");
            }
        }

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        Logger.LogInformation("Password authenticated user {UserId}.", user.Id);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The login page is where a password is asked for, and the cookie scheme already sends people
    /// there; this scheme answers 401 so that nothing ever redirects to it by mistake.
    /// </remarks>
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
