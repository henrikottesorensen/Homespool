using System;
using System.Globalization;
using System.Security.Claims;
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
/// What a verified credential becomes: the cookies a sign-in writes and clears, in one place. A
/// scheme proves a credential; a page decides what the proof is worth; this is the only thing that
/// turns the decision into a session, a pending second factor or a remembered browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transcribed from the framework's <c>SignInManager</c> at v10.0.11</b> - <c>SignInWithClaimsAsync</c>,
/// <c>SignInOrTwoFactorAsync</c>'s cookie half, <c>DoTwoFactorSignInAsync</c>'s clean-up,
/// <c>RefreshSignInAsync</c>, <c>RememberTwoFactorClientAsync</c>, <c>ForgetTwoFactorClientAsync</c> and
/// <c>SignOutAsync</c> - with the state machine left behind. The manager decided <i>and</i> wrote; here
/// the page decides, and each method writes exactly the cookies its name says.
/// </para>
/// <para>
/// <b>The session principal is the scheme's own.</b> The claims factory built it and the handler tagged
/// it with its method, so it goes on the application cookie as it is rather than through the factory a
/// second time. A completed second factor adds the provider it was pending for, as
/// <see cref="JwtClaimTypes.IdentityProvider"/>. A refresh rebuilds the principal from the account
/// and carries the method and provider over, where the framework's refresh keeps them and its stamp
/// validator drops them.
/// </para>
/// <para>
/// <b>The pending and remembered cookies carry <see cref="JwtClaimTypes.Subject"/></b>, not the
/// framework's <c>ClaimTypes.Name</c>: both are written and read on this side now, and the house
/// spells claims the JWT way. The remembered cookie also carries the account's security stamp, as the
/// framework's does, so a stamp change revokes it - see <see cref="RememberedBrowserStampValidator"/>.
/// </para>
/// </remarks>
public sealed class LocalSignIn
{
    private readonly UserManager<HSUser> _users;
    private readonly IUserClaimsPrincipalFactory<HSUser> _claimsFactory;
    private readonly LocalSignInRules _rules;
    private readonly IdentityOptions _options;
    private readonly ILogger<LocalSignIn> _logger;

    public LocalSignIn(UserManager<HSUser> users,
                       IUserClaimsPrincipalFactory<HSUser> claimsFactory,
                       LocalSignInRules rules,
                       IOptions<IdentityOptions> options,
                       ILogger<LocalSignIn> logger)
    {
        _users = users;
        _claimsFactory = claimsFactory;
        _rules = rules;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Signs <paramref name="principal"/> in on the application cookie, clearing the external and
    /// pending cookies a sign-in supersedes. <paramref name="loginProvider"/> is the provider the
    /// sign-in came through, or that a completed second factor was pending for, when there was one.
    /// </summary>
    public async Task SignInAsync(HttpContext context, ClaimsPrincipal principal, bool isPersistent, string? loginProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);

        if (loginProvider is not null && principal.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(JwtClaimTypes.IdentityProvider, loginProvider));
        }

        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);

        await SignInCoreAsync(context, principal, new AuthenticationProperties { IsPersistent = isPersistent });
    }

    /// <summary>
    /// Signs <paramref name="user"/> in on the application cookie with a principal the claims factory
    /// builds: for a sign-in no scheme proved, such as the one that follows creating the account.
    /// </summary>
    public async Task SignInAsync(HttpContext context, HSUser user, bool isPersistent, string? loginProvider = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        await SignInAsync(context, await _claimsFactory.CreateAsync(user), isPersistent, loginProvider);
    }

    /// <summary>
    /// Re-issues the signed-in session for <paramref name="user"/> after something on the account
    /// changed - the stamp, a claim - keeping the cookie's own properties and the method and provider
    /// the session was signed in with. Refuses, with <see langword="false"/>, when nobody is signed in
    /// or somebody else is: a refresh is not a way to change users.
    /// </summary>
    public async Task<bool> RefreshSignInAsync(HttpContext context, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        AuthenticateResult session = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        if (!session.Succeeded || session.Principal.Identity?.IsAuthenticated != true)
        {
            _logger.LogError("A sign-in refresh was refused because nobody is signed in; sign in instead.");

            return false;
        }

        string? signedIn = _users.GetUserId(session.Principal);
        string refreshed = await _users.GetUserIdAsync(user);

        if (signedIn is null || !string.Equals(signedIn, refreshed, StringComparison.Ordinal))
        {
            _logger.LogError("A sign-in refresh was refused because a different account is signed in; sign in instead.");

            return false;
        }

        await SignInCoreAsync(context, await RebuiltPrincipalAsync(user, session.Principal), session.Properties);

        return true;
    }

    /// <summary>
    /// A fresh principal for <paramref name="user"/> from the claims factory, carrying the method and
    /// provider claims <paramref name="current"/> was signed in with.
    /// </summary>
    public async Task<ClaimsPrincipal> RebuiltPrincipalAsync(HSUser user, ClaimsPrincipal current)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(current);

        ClaimsPrincipal fresh = await _claimsFactory.CreateAsync(user);

        if (fresh.Identity is ClaimsIdentity identity)
        {
            foreach (Claim carried in current.FindAll(claim => claim.Type is JwtClaimTypes.AuthenticationMethod or JwtClaimTypes.IdentityProvider))
            {
                identity.AddClaim(new Claim(carried.Type, carried.Value));
            }
        }

        return fresh;
    }

    /// <summary>
    /// Whether <paramref name="user"/> still owes a second factor on this browser: two-factor is on,
    /// the account has a provider to answer with, and this browser was not remembered after one.
    /// </summary>
    public async Task<bool> OwesSecondFactorAsync(HttpContext context, HSUser user)
    {
        if (!_users.SupportsUserTwoFactor || !await _users.GetTwoFactorEnabledAsync(user))
        {
            return false;
        }

        if ((await _users.GetValidTwoFactorProvidersAsync(user)).Count == 0)
        {
            return false;
        }

        return !await _rules.IsTwoFactorClientRememberedAsync(context, user);
    }

    /// <summary>
    /// Records that <paramref name="user"/> passed its first factor and owes its second: the pending
    /// cookie, carrying the provider the first factor came through when it was not a password.
    /// </summary>
    public Task BeginSecondFactorAsync(HttpContext context, HSUser user, string? loginProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, LocalSignInRules.PendingTwoFactor(user, loginProvider));
    }

    /// <summary>
    /// Remembers this browser for <paramref name="user"/>, so the next sign-in owes no second factor.
    /// The cookie carries the account's security stamp, so a stamp change forgets every browser.
    /// </summary>
    public async Task RememberClientAsync(HttpContext context, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        ClaimsIdentity identity = new(IdentityConstants.TwoFactorRememberMeScheme);
        identity.AddClaim(new Claim(JwtClaimTypes.Subject, user.Id.ToString(CultureInfo.InvariantCulture)));

        if (_users.SupportsUserSecurityStamp)
        {
            identity.AddClaim(new Claim(_options.ClaimsIdentity.SecurityStampClaimType, await _users.GetSecurityStampAsync(user)));
        }

        await context.SignInAsync(IdentityConstants.TwoFactorRememberMeScheme,
                                  new ClaimsPrincipal(identity),
                                  new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>Forgets this browser, so the next sign-in asks for the second factor again.</summary>
    public Task ForgetClientAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.SignOutAsync(IdentityConstants.TwoFactorRememberMeScheme);
    }

    /// <summary>
    /// Ends the session: the application cookie, and the external and pending cookies a sign-in in
    /// progress may have left. The remembered browser stays remembered, as the framework leaves it.
    /// </summary>
    public async Task SignOutAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
    }

    private static async Task SignInCoreAsync(HttpContext context, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
        await context.SignInAsync(IdentityConstants.ApplicationScheme, principal, properties);

        // So that the rest of this request sees the person as signed in, as the framework does.
        context.User = principal;
    }
}
