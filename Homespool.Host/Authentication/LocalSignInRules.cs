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
    /// How much longer the account's step-ups are backed off, or <see langword="null"/> when they are
    /// not: the <see cref="LimitedAction.StepUp"/> counter, checked before a step-up's credential is
    /// compared so a backed-off session cannot learn whether its guesses were close.
    /// </summary>
    public Task<TimeSpan?> StepUpBackoffAsync(HSUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _stepUps.RemainingLockoutAsync(user.Id, LimitedAction.StepUp, _time.GetUtcNow(), cancellationToken);
    }

    /// <summary>Counts a wrong step-up credential against the account's step-up backoff - and nothing else.</summary>
    public Task RecordStepUpFailureAsync(HSUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _stepUps.RecordFailedAttemptAsync(user.Id, LimitedAction.StepUp, _time.GetUtcNow(), cancellationToken);
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
    /// The framework's pre-sign-in check: <see cref="SignInRefusal.NotAllowed"/> for an account that
    /// may not sign in, <see cref="SignInRefusal.LockedOut"/> for one that is locked out, or
    /// <see langword="null"/> when a credential may be believed.
    /// </summary>
    public async Task<SignInRefusal?> PreSignInCheckAsync(HSUser user)
    {
        if (!await CanSignInAsync(user))
        {
            return SignInRefusal.NotAllowed;
        }

        if (_users.SupportsUserLockout && await _users.IsLockedOutAsync(user))
        {
            return SignInRefusal.LockedOut;
        }

        return null;
    }

    /// <summary>
    /// Whether the account may sign in at all: the confirmed-account rule, and the email and phone
    /// ones the framework also offers, read from <see cref="IdentityOptions.SignIn"/>.
    /// </summary>
    public async Task<bool> CanSignInAsync(HSUser user)
    {
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
    /// Records a failed attempt against the account's lockout, and says whether that locked it out.
    /// </summary>
    public async Task<bool> RecordFailureAsync(HSUser user)
    {
        if (!_users.SupportsUserLockout)
        {
            return false;
        }

        // A failed increment is a wrong credential and nothing more, as the framework has it: a
        // concurrency failure here could be an attacker trying to slip past the count, so the
        // attempt is refused, but it is not a lockout and must not send the person to the lockout
        // page - it costs a legitimate person one retry.
        IdentityResult incremented = await _users.AccessFailedAsync(user);

        return incremented.Succeeded && await _users.IsLockedOutAsync(user);
    }

    /// <summary>
    /// The principal the pending two-factor cookie carries: the account that passed its first factor
    /// and owes its second, and the provider that factor came through when it was not a password.
    /// </summary>
    public static ClaimsPrincipal PendingTwoFactor(HSUser user, string? loginProvider = null)
    {
        ClaimsIdentity identity = new(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(JwtClaimTypes.Subject, user.Id.ToString(CultureInfo.InvariantCulture)));

        if (loginProvider is not null)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.IdentityProvider, loginProvider));
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
