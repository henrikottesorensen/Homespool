using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// Re-checks a cookie's principal against the account's security stamp every time the cookie is read:
/// a changed stamp - a new password, a re-keyed authenticator, a removed login - is what forgets every
/// remembered browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transcribed from the framework's <c>SecurityStampValidator&lt;TUser&gt;</c> at v10.0.11</b>, which
/// took a <c>SignInManager</c> for three things: to find the account the principal names, to compare
/// the stamp, and to sign out on a mismatch. The first two are <see cref="UserManager{TUser}"/> here;
/// the third is not kept, below. The <c>OnRefreshingPrincipal</c> hook is not carried over; nothing in
/// the application set it. Nor is the framework's validation interval, below.
/// </para>
/// <para>
/// <b>A mismatch forgets only the cookie being checked, not the session beside it.</b> The framework
/// signs the whole browser out, and that has nothing to add here: the application cookie answers to
/// <see cref="SessionStampValidator"/> on every request, which already ends a session when the stamp
/// moved anywhere but in that session's own request. What signing out would add is harm. A page that
/// moves the stamp refreshes its own session through <see cref="LocalSignIn.RefreshSignInAsync"/> and
/// leaves the remembered cookie on the old stamp, so the next read of it would end the session the
/// refresh had just kept; and a browser shared by two accounts would sign one out over the other's
/// stale cookie.
/// </para>
/// <para>
/// <b>Only the remembered-browser cookie is checked this way now.</b> The application cookie is checked
/// on every request against its session row by <see cref="SessionStampValidator"/>, which compares the
/// stamp as part of that and rebuilds nothing.
/// </para>
/// <para>
/// <b>Every read, not once an interval has passed.</b> A remembered browser is what spares a sign-in its
/// second factor, so any interval is a window after an authenticator is reset or turned off in which a
/// browser remembered before that still skips the code, and the password alone signs in. It is read
/// only when a sign-in asks whether a second factor is owed, by the two-factor settings page, and by
/// the rename and the address change before they re-issue it, so checking it every time costs one
/// account read on each.
/// </para>
/// </remarks>
public abstract class StampValidator : ISecurityStampValidator
{
    protected StampValidator(IOptions<IdentityOptions> identity,
                             UserManager<HSUser> users,
                             ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(identity);

        Identity = identity.Value;
        Users = users;
        Logger = logger;
    }

    protected IdentityOptions Identity { get; }

    protected UserManager<HSUser> Users { get; }

    protected ILogger Logger { get; }

    /// <inheritdoc/>
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HSUser? user = await VerifiedAccountAsync(context.Principal);

        if (user is null)
        {
            Logger.LogDebug("Security stamp validation failed; rejecting the cookie and forgetting it.");

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);

            return;
        }

        await VerifiedAsync(user, context);
    }

    /// <summary>
    /// The account <paramref name="principal"/> names, when its stamp claim still matches the store's,
    /// or <see langword="null"/>.
    /// </summary>
    protected abstract Task<HSUser?> VerifiedAccountAsync(ClaimsPrincipal? principal);

    /// <summary>What a verified cookie gets: a refreshed principal, or nothing.</summary>
    protected abstract Task VerifiedAsync(HSUser user, CookieValidatePrincipalContext context);

    /// <summary>
    /// Whether <paramref name="principal"/>'s stamp claim is the account's current stamp. A store that
    /// keeps no stamp verifies every principal, as the framework's does.
    /// </summary>
    protected async Task<bool> StampMatchesAsync(HSUser user, ClaimsPrincipal principal)
    {
        if (!Users.SupportsUserSecurityStamp)
        {
            return true;
        }

        string? claimed = principal.FindFirstValue(Identity.ClaimsIdentity.SecurityStampClaimType);

        return claimed is not null && string.Equals(claimed, await Users.GetSecurityStampAsync(user), StringComparison.Ordinal);
    }
}
