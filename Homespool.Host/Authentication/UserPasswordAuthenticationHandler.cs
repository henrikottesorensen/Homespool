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
/// <b>One change to the framework's order: the attempt is counted before the password is
/// compared</b> (<see cref="LocalSignInRules.TakeAttemptAsync"/>), and a right password gives it back.
/// Checking the lockout, comparing and then counting let a burst of parallel posts all pass the check
/// before any had counted, and every one was compared. Counted first, the post that reaches the
/// threshold locks the account out while it is still being compared, and the rest are refused unread.
/// </para>
/// <para>
/// <b>The one scheme that names its own account - at login.</b> A <see cref="UserPasswordCredential"/>
/// resolves by username and then by address, in one field, which is safe because a username may not
/// contain <c>@</c>. An identifier nobody holds is refused after a decoy verification, so a miss costs
/// what a wrong password costs, and the wording is the same for both.
/// </para>
/// <para>
/// <b>Every refusal pays a verification, and one gap survives that.</b> Two branches never reach the
/// password comparison and spend the same decoy an unknown identifier does: an account that exists but
/// may not sign in - deactivated, or already locked out - returns from the pre-sign-in check first, and
/// an account that signs in only with a provider has no stored hash for the comparison to run against.
/// Without it either branch answers in microseconds where every other outcome costs a PBKDF2, which is
/// the same oracle in a different place - and the provider-only one is the more telling of the two,
/// since a cheap answer there names the door as well as the account. What a decoy cannot cover is the
/// response itself: <c>Login</c> redirects a locked-out account to <c>Lockout</c> while re-rendering
/// the form for everything else, which confirms the account exists - deliberately, in exchange for
/// telling its owner why they are being turned away. The thing that would blunt the guessing that
/// finds it, the per-address <c>SignIn</c> rate limit, is inert where no proxy is trusted. So: one
/// message, and one hash on every path; one status code still differs, by decision.
/// </para>
/// <para>
/// <b>On a step-up the account is the session's.</b> A <see cref="PasswordCredential"/> carries only
/// the password, and it is checked against the signed-in account; with nobody signed in it is refused
/// unread. A wrong one backs off the account's step-ups (<see cref="LocalSignInRules.TakeStepUpAsync"/>)
/// and never touches the account lockout, which is the login page's: a session holder guessing here
/// must not be able to lock the owner out of signing in. A page that wants to confirm an act asks for
/// this and never for a login.
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
        PasswordCredential? stepUp = Context.Features.Get<PasswordCredential>();

        // No credential presented: this scheme has nothing to say about the request.
        if (credential is null && stepUp is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (credential is not null && stepUp is not null)
        {
            return SignInRefusals.Fail(SignInRefusal.Invalid, "A login and a step-up cannot be presented together.");
        }

        if (stepUp is not null)
        {
            return await StepUpAsync(stepUp.Password);
        }

        (string? login, string? password) = credential!;

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
            // The decoy the unknown-identifier branch pays, for the same reason: this refusal returns
            // before the password is ever compared, so without it an account that may not sign in is
            // the cheap answer among expensive ones, and cheap is measurable from outside.
            PasswordVerificationDecoy.Verify(_hasher, password);

            Logger.LogInformation("Password sign-in refused for user {UserId}: {Refusal}.", user.Id, refusal);

            return SignInRefusals.Fail(refusal, "The account may not sign in.");
        }

        AttemptTicket attempt = await _rules.TakeAttemptAsync(user);

        if (!attempt.Taken)
        {
            // Locked out by an attempt counted since the check above - a parallel one. The same decoy,
            // for the same reason.
            PasswordVerificationDecoy.Verify(_hasher, password);

            Logger.LogInformation("Password sign-in refused for user {UserId}: {Refusal}.", user.Id, SignInRefusal.LockedOut);

            return SignInRefusals.Fail(SignInRefusal.LockedOut, "The account may not sign in.");
        }

        // An account that signs in with a provider stores no hash, and the comparison below answers
        // false without running one - so without a decoy it is the cheap answer among expensive ones,
        // and cheap here says more than the branches above give away: the account exists *and* the
        // provider is the door. Spending one when there is nothing to compare against keeps the cost
        // the same as a wrong password's; spending it on every failed comparison instead would cost a
        // real wrong password two hashes and open the gap the other way round.
        bool hasPassword = await _users.HasPasswordAsync(user);

        if (!hasPassword)
        {
            PasswordVerificationDecoy.Verify(_hasher, password);
        }

        if (!hasPassword || !await _users.CheckPasswordAsync(user, password))
        {
            // Already counted; whether this was the attempt that locked the account out is all that
            // is left to say.
            bool lockedOut = attempt.LockoutImposed is not null;

            // The reason is a property rather than two message texts because the refusal is one: an
            // operator asked why somebody cannot sign in is owed "this account signs in with a
            // provider" rather than "wrong password", which is the answer that sends them looking for
            // a password that does not exist.
            string reason = hasPassword ?
                "wrong password" :
                "the account has no password and signs in with a provider";

            Logger.LogInformation("Password sign-in refused for user {UserId}: {Reason}{LockedOut}.",
                                  user.Id,
                                  reason,
                                  lockedOut ? ", now locked out" : string.Empty);

            return SignInRefusals.Fail(lockedOut ? SignInRefusal.LockedOut : SignInRefusal.Invalid, "Invalid login attempt.");
        }

        // The framework's quirk, kept: while a second factor is still owed, the failed count stands
        // until the code is right, unless this browser was remembered after one - so only this
        // attempt is given back.
        bool resetCount = !await _users.GetTwoFactorEnabledAsync(user) || await _rules.IsTwoFactorClientRememberedAsync(Context, user);

        await _rules.AttemptPassedAsync(user, attempt, resetCount);

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        Logger.LogInformation("Password authenticated user {UserId}.", user.Id);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>The signed-in account's password, checked again for an act the session alone may not do.</summary>
    private async Task<AuthenticateResult> StepUpAsync(string? password)
    {
        HSUser? user = await _rules.SignedInAccountAsync(Context);

        if (user is null)
        {
            Logger.LogInformation("Password step-up refused: nobody is signed in.");

            return SignInRefusals.Fail(SignInRefusal.Invalid, "There is no signed-in account to check a password for.");
        }

        if (string.IsNullOrEmpty(password))
        {
            return SignInRefusals.Fail(SignInRefusal.Invalid, "A password is required.");
        }

        if (await _rules.PreSignInCheckAsync(user) is { } refusal)
        {
            Logger.LogInformation("Password step-up refused for user {UserId}: {Refusal}.", user.Id, refusal);

            return SignInRefusals.Fail(refusal, "The account may not sign in.", await _rules.RemainingLockoutAsync(user));
        }

        // The step-up's own backoff, counted before the password is compared and instead of the
        // account lockout: a session holder guessing here must not lock the owner out.
        if ((await _rules.TakeStepUpAsync(user, Context.RequestAborted)).BackedOff is { } backedOff)
        {
            Logger.LogInformation("Password step-up refused for user {UserId}: backed off for {Remaining}.", user.Id, backedOff);

            return SignInRefusals.Fail(SignInRefusal.LockedOut, "Too many wrong step-ups.", backedOff);
        }

        if (!await _users.CheckPasswordAsync(user, password))
        {
            Logger.LogInformation("Password step-up refused for user {UserId}: wrong password.", user.Id);

            return SignInRefusals.Fail(SignInRefusal.Invalid, "Invalid password.");
        }

        await _rules.ResetStepUpAsync(user, Context.RequestAborted);

        ClaimsPrincipal principal = await _claimsFactory.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.AuthenticationMethod, AuthenticationMethod));
        }

        Logger.LogInformation("Password step-up passed for user {UserId}.", user.Id);

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
