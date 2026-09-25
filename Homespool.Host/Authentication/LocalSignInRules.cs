using System;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

using Homespool.Host.Accounts;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// The rules every local credential scheme applies before it believes a credential, and the two
/// Identity cookies those schemes read: whether an account may sign in at all, whether it is locked
/// out, which account is pending its second factor, and whether this browser was remembered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transcribed from the framework's <c>SignInManager</c> at v10.0.11</b> - <c>CanSignInAsync</c>,
/// <c>IsLockedOut</c>, <c>StoreTwoFactorInfo</c>, <c>RetrieveTwoFactorInfoAsync</c> and
/// <c>IsTwoFactorClientRememberedAsync</c> - with the generics resolved and the state machine left
/// behind. The schemes that call these compose them; nothing here decides what a verified credential
/// is worth.
/// </para>
/// <para>
/// <b>A step-up has its own counter.</b> A wrong password or code typed inside a session backs off
/// the account's step-ups through <see cref="AttemptLimiter"/> under <see cref="LimitedAction.StepUp"/>,
/// exponentially and self-healing, and never touches the account lockout: a session holder guessing
/// at a step-up must not be able to lock the owner out of signing in, which is how the owner takes
/// the session back. The account lockout is the login page's, where a wrong credential is a guess
/// from outside.
/// </para>
/// <para>
/// <b>The pending and remembered cookies carry the account as <see cref="JwtClaimTypes.Subject"/></b>
/// and the pending one its provider as <see cref="JwtClaimTypes.IdentityProvider"/>, the house's JWT
/// spelling rather than the framework's <c>ClaimTypes.Name</c>. <see cref="LocalSignIn"/> writes both,
/// so nothing reads a cookie the other side wrote.
/// </para>
/// </remarks>
public sealed class LocalSignInRules
{
    private readonly UserManager<HSUser> _users;
    private readonly IUserConfirmation<HSUser> _confirmation;
    private readonly AttemptLimiter _stepUps;
    private readonly IdentityOptions _options;
    private readonly TimeProvider _time;

    public LocalSignInRules(UserManager<HSUser> users,
                            IUserConfirmation<HSUser> confirmation,
                            AttemptLimiter stepUps,
                            IOptions<IdentityOptions> options,
                            TimeProvider? time = null)
    {
        _users = users;
        _confirmation = confirmation;
        _stepUps = stepUps;
        _options = options.Value;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Counts a step-up against the account's step-up backoff before its credential is compared - and
    /// nothing else - or refuses it, uncounted, while step-ups are backed off. A backed-off session
    /// cannot learn whether its guesses were close, and parallel step-ups cannot all be compared on a
    /// count none of them has written. A wrong credential then needs nothing further; a right one
    /// clears the backoff with <see cref="ResetStepUpAsync"/>.
    /// </summary>
    public Task<AttemptTicket> TakeStepUpAsync(HSUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _stepUps.TakeAttemptAsync(user.Id, LimitedAction.StepUp, _time.GetUtcNow(), cancellationToken);
    }

    /// <summary>A right step-up credential clears the step-up backoff.</summary>
    public Task ResetStepUpAsync(HSUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _stepUps.ResetAsync(user.Id, LimitedAction.StepUp, cancellationToken);
    }

    /// <summary>
    /// How much longer <paramref name="user"/>'s lockout lasts, for a page that says so; zero when
    /// the account is not locked out.
    /// </summary>
    public async Task<TimeSpan> RemainingLockoutAsync(HSUser user)
    {
        if (!_users.SupportsUserLockout || !await _users.IsLockedOutAsync(user))
        {
            return TimeSpan.Zero;
        }

        DateTimeOffset? end = await _users.GetLockoutEndDateAsync(user);
        TimeSpan remaining = end is null ? TimeSpan.Zero : end.Value - _time.GetUtcNow();

        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>
    /// Whether <paramref name="principal"/> is a signed-in person: an identity authenticated by the
    /// application cookie, as distinct from a printer or an API client under its own scheme.
    /// </summary>
    /// <remarks>The framework's <c>SignInManager.IsSignedIn</c>, unchanged: the claims factory names every session identity after the application scheme.</remarks>
    public static bool IsSignedIn(ClaimsPrincipal? principal)
    {
        return principal?.Identities.Any(identity => string.Equals(identity.AuthenticationType, IdentityConstants.ApplicationScheme, StringComparison.Ordinal)) == true;
    }

    /// <summary>
    /// The framework's pre-sign-in check for a guessable secret: <see cref="SignInRefusal.NotAllowed"/>
    /// for an account that may not sign in, <see cref="SignInRefusal.LockedOut"/> for one that is
    /// locked out, or <see langword="null"/> when the credential may be compared.
    /// </summary>
    /// <remarks>
    /// <b>The lockout is the password path's, and only a handler that compares a guessable secret
    /// consults it</b> - the password, the authenticator code and the recovery code, whose wrong
    /// answers are what it counts. A passkey, an API token or a provider's answer cannot be guessed at
    /// the login form, so they take <see cref="StandingCheckAsync"/> instead: otherwise whoever knows
    /// a username could keep every credential on the account failing with one wrong password every
    /// five minutes, and stop its scripts and its owner's own way back in.
    /// </remarks>
    public async Task<SignInRefusal?> PreSignInCheckAsync(HSUser user)
    {
        if (await StandingCheckAsync(user) is { } refusal)
        {
            return refusal;
        }

        if (_users.SupportsUserLockout && await _users.IsLockedOutAsync(user))
        {
            return SignInRefusal.LockedOut;
        }

        return null;
    }

    /// <summary>
    /// The account's standing alone: <see cref="SignInRefusal.NotAllowed"/> for an account that may
    /// not sign in - deactivated, or unconfirmed where confirmation is required - or
    /// <see langword="null"/> when it may. Never the lockout; <see cref="PreSignInCheckAsync"/> says why.
    /// </summary>
    public async Task<SignInRefusal?> StandingCheckAsync(HSUser user)
    {
        return await CanSignInAsync(user) ? null : SignInRefusal.NotAllowed;
    }

    /// <summary>
    /// Whether the account may sign in at all: this deployment's deactivation, then the
    /// confirmed-account rule and the email and phone ones the framework also offers, read from
    /// <see cref="IdentityOptions.SignIn"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The deactivation check is here rather than in each scheme.</b> Every credential this
    /// application accepts - password, authenticator code, recovery code, passkey, a provider's
    /// assertion, and both personal-access-token headers - reaches <see cref="PreSignInCheckAsync"/>
    /// before it is believed, so one condition in this method refuses all of them. A scheme added later
    /// gets the rule by calling what its siblings call, rather than by remembering to.
    /// </para>
    /// <para>
    /// <b>It covers a credential being presented, and nothing else.</b> A closed account can still be
    /// reached by what presents none: a page redeeming an emailed token (refused in
    /// <see cref="Accounts.HSUserManager.VerifyUserTokenAsync"/>), a role or membership read (asked of
    /// <c>Administrators.Open</c> and <c>Memberships.Open</c>), work done later on a stored user id,
    /// and anything authorised once that keeps running. Each needs its own answer; this method is not it.
    /// </para>
    /// </remarks>
    public async Task<bool> CanSignInAsync(HSUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.DeactivatedAt is not null)
        {
            return false;
        }

        if (_options.SignIn.RequireConfirmedEmail && !await _users.IsEmailConfirmedAsync(user))
        {
            return false;
        }

        if (_options.SignIn.RequireConfirmedPhoneNumber && !await _users.IsPhoneNumberConfirmedAsync(user))
        {
            return false;
        }

        if (_options.SignIn.RequireConfirmedAccount && !await _confirmation.IsConfirmedAsync(_users, user))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Counts an attempt at the password or a sign-in code against the account's lockout before it is
    /// compared, or refuses it, uncounted, while the account is locked out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Counted first so the lockout check is also a reservation.</b> Checking, comparing and then
    /// counting let every request of a parallel burst pass the check before any had counted, and each
    /// was compared: the account locked out, but only after the whole burst had been tried. Here the
    /// attempt that reaches the threshold locks the account out while it is still being compared.
    /// </para>
    /// <para>
    /// A wrong credential needs nothing further - <see cref="AttemptTicket.LockoutImposed"/> says
    /// whether it was the one that locked the account out. A right one calls
    /// <see cref="AttemptPassedAsync"/>. After <see cref="PreSignInCheckAsync"/>, which still says why
    /// an account may not sign in at all.
    /// </para>
    /// </remarks>
    public async Task<AttemptTicket> TakeAttemptAsync(HSUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _users.SupportsUserLockout ?
            await Manager().TakeAccessAttemptAsync(user) :
            AttemptTicket.Counted(lockoutImposed: null);
    }

    /// <summary>
    /// Settles an attempt <see cref="TakeAttemptAsync"/> counted whose credential was right: the failed
    /// count cleared, or - with <paramref name="resetCount"/> false, while a second factor is still
    /// owed - only this attempt given back. Either way a lockout this attempt imposed is lifted.
    /// </summary>
    public Task AttemptPassedAsync(HSUser user, AttemptTicket attempt, bool resetCount)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(attempt);

        if (!_users.SupportsUserLockout)
        {
            return Task.CompletedTask;
        }

        return resetCount ?
            Manager().ResetAccessFailedCountAsync(user, attempt) :
            Manager().ReturnAccessAttemptAsync(user, attempt);
    }

    private HSUserManager Manager()
    {
        return _users as HSUserManager ??
               throw new NotSupportedException("Counting a sign-in attempt before it is compared needs HSUserManager.");
    }

    /// <summary>
    /// The principal the pending two-factor cookie carries: the account that passed its first factor
    /// and owes its second, the provider that factor came through when it was not a password, and the
    /// account's security stamp as it stood, which the cookie is checked against on every read.
    /// </summary>
    public static ClaimsPrincipal PendingTwoFactor(HSUser user, string? loginProvider = null, Claim? securityStamp = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        ClaimsIdentity identity = new(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(JwtClaimTypes.Subject, user.Id.ToString(CultureInfo.InvariantCulture)));

        if (loginProvider is not null)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.IdentityProvider, loginProvider));
        }

        if (securityStamp is not null)
        {
            identity.AddClaim(securityStamp);
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// The account the pending two-factor cookie names, or <see langword="null"/> when no first factor
    /// has been passed on this browser.
    /// </summary>
    public async Task<HSUser?> PendingTwoFactorAccountAsync(HttpContext context)
    {
        AuthenticateResult pending = await context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        string? userId = pending.Principal?.FindFirstValue(JwtClaimTypes.Subject);

        return userId is null ? null : await _users.FindByIdAsync(userId);
    }

    /// <summary>
    /// The provider the pending account's first factor came through, or <see langword="null"/> when it
    /// was a password or nothing is pending.
    /// </summary>
    public async Task<string?> PendingLoginProviderAsync(HttpContext context)
    {
        AuthenticateResult pending = await context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);

        return pending.Principal?.FindFirstValue(JwtClaimTypes.IdentityProvider);
    }

    /// <summary>
    /// The account the signed-in session belongs to, or <see langword="null"/> when there is none:
    /// the request's principal when the pipeline already signed a person in, else the application
    /// cookie read directly.
    /// </summary>
    public async Task<HSUser?> SignedInAccountAsync(HttpContext context)
    {
        if (IsSignedIn(context.User))
        {
            return await _users.GetUserAsync(context.User);
        }

        AuthenticateResult session = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        return session.Principal is null ? null : await _users.GetUserAsync(session.Principal);
    }

    /// <summary>
    /// Whether this browser was remembered after a second factor for <paramref name="user"/>: the
    /// remembered-machine cookie names the account.
    /// </summary>
    public async Task<bool> IsTwoFactorClientRememberedAsync(HttpContext context, HSUser user)
    {
        AuthenticateResult remembered = await context.AuthenticateAsync(IdentityConstants.TwoFactorRememberMeScheme);

        return remembered.Principal?.FindFirstValue(JwtClaimTypes.Subject) == user.Id.ToString(CultureInfo.InvariantCulture);
    }
}
