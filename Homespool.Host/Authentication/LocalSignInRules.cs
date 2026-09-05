using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

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
/// <b>The pending cookie keeps the framework's claim shape</b>: the account id as
/// <see cref="ClaimTypes.Name"/> and an optional provider as <see cref="ClaimTypes.AuthenticationMethod"/>,
/// so a cookie written by either side reads on the other while the pages move across.
/// </para>
/// </remarks>
public sealed class LocalSignInRules
{
    private readonly UserManager<HSUser> _users;
    private readonly IUserConfirmation<HSUser> _confirmation;
    private readonly IdentityOptions _options;

    public LocalSignInRules(UserManager<HSUser> users,
                            IUserConfirmation<HSUser> confirmation,
                            IOptions<IdentityOptions> options)
    {
        _users = users;
        _confirmation = confirmation;
        _options = options.Value;
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

        // A failed increment is treated as a wrong credential by the framework too: a concurrency
        // failure here could be an attacker trying to slip past the count, and a refusal costs a
        // legitimate person one retry.
        IdentityResult incremented = await _users.AccessFailedAsync(user);

        return !incremented.Succeeded || await _users.IsLockedOutAsync(user);
    }

    /// <summary>
    /// The principal the pending two-factor cookie carries: the account that passed its first factor
    /// and owes its second, in the framework's own claim shape.
    /// </summary>
    public static ClaimsPrincipal PendingTwoFactor(HSUser user, string? loginProvider = null)
    {
        ClaimsIdentity identity = new(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Id.ToString(CultureInfo.InvariantCulture)));

        if (loginProvider is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, loginProvider));
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
        string? userId = pending.Principal?.FindFirstValue(ClaimTypes.Name);

        return userId is null ? null : await _users.FindByIdAsync(userId);
    }

    /// <summary>
    /// The account the signed-in session belongs to, or <see langword="null"/> when there is none.
    /// </summary>
    public async Task<HSUser?> SignedInAccountAsync(HttpContext context)
    {
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

        return remembered.Principal?.FindFirstValue(ClaimTypes.Name) == user.Id.ToString(CultureInfo.InvariantCulture);
    }
}
