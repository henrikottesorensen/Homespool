using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

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
/// <c>RememberTwoFactorClientAsync</c>, <c>ForgetTwoFactorClientAsync</c> and <c>SignOutAsync</c> -
/// with the state machine left behind. The manager decided <i>and</i> wrote; here the page decides,
/// and each method writes exactly the cookies its name says.
/// </para>
/// <para>
/// <b>The session principal is the scheme's own.</b> The claims factory built it and the handler tagged
/// it with its method, so it goes on the application cookie as it is rather than through the factory a
/// second time. A completed second factor adds the provider it was pending for, as
/// <see cref="JwtClaimTypes.IdentityProvider"/>.
/// </para>
/// <para>
/// <b>The pending and remembered cookies carry <see cref="JwtClaimTypes.Subject"/></b>, not the
/// framework's <c>ClaimTypes.Name</c>: both are written and read on this side now, and the house
/// spells claims the JWT way. A browser remembered by the framework before this change is asked for
/// its code once more, and then remembered again in the new shape.
/// </para>
/// </remarks>
public sealed class LocalSignIn
{
    private readonly UserManager<HSUser> _users;
    private readonly LocalSignInRules _rules;

    public LocalSignIn(UserManager<HSUser> users, LocalSignInRules rules)
    {
        _users = users;
        _rules = rules;
    }

    /// <summary>
    /// Signs <paramref name="principal"/> in on the application cookie, clearing the external and
    /// pending cookies a sign-in supersedes. <paramref name="loginProvider"/> is the provider a
    /// completed second factor was pending for, when there was one.
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

        await context.SignInAsync(IdentityConstants.ApplicationScheme,
                                  principal,
                                  new AuthenticationProperties { IsPersistent = isPersistent });

        // So that the rest of this request sees the person as signed in, as the framework does.
        context.User = principal;
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

    /// <summary>Remembers this browser for <paramref name="user"/>, so the next sign-in owes no second factor.</summary>
    public Task RememberClientAsync(HttpContext context, HSUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);

        ClaimsIdentity identity = new(IdentityConstants.TwoFactorRememberMeScheme);
        identity.AddClaim(new Claim(JwtClaimTypes.Subject, user.Id.ToString(CultureInfo.InvariantCulture)));

        return context.SignInAsync(IdentityConstants.TwoFactorRememberMeScheme,
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
}
