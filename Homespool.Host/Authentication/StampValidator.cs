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
/// Re-checks a cookie's principal against the account's security stamp once the cookie is older than
/// <see cref="SecurityStampValidatorOptions.ValidationInterval"/>: a changed stamp - a new password,
/// a re-keyed authenticator, a removed login - is what signs every other browser out, and this is
/// where it does so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transcribed from the framework's <c>SecurityStampValidator&lt;TUser&gt;</c> at v10.0.11</b>, which
/// took a <c>SignInManager</c> for three things: to find the account the principal names, to compare
/// the stamp, and to sign out on a mismatch. Those are <see cref="UserManager{TUser}"/> and
/// <see cref="LocalSignIn"/> here. The <c>OnRefreshingPrincipal</c> hook is not carried over; nothing
/// in the application set it.
/// </para>
/// <para>
/// <b>A mismatch ends the whole session, not just the cookie being checked</b>: the application,
/// external and pending cookies go, and the remembered browser with them, whichever of the two
/// validated cookies noticed. That is the framework's behaviour, kept: a stale stamp on any of them
/// means the account changed underneath this browser.
/// </para>
/// </remarks>
public abstract class StampValidator : ISecurityStampValidator
{
    private readonly SecurityStampValidatorOptions _options;

    protected StampValidator(IOptions<SecurityStampValidatorOptions> options,
                             IOptions<IdentityOptions> identity,
                             UserManager<HSUser> users,
                             LocalSignIn signIn,
                             ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);

        _options = options.Value;
        Identity = identity.Value;
        Users = users;
        SignIn = signIn;
        Logger = logger;
        Time = _options.TimeProvider ?? TimeProvider.System;
    }

    protected IdentityOptions Identity { get; }

    protected UserManager<HSUser> Users { get; }

    protected LocalSignIn SignIn { get; }

    protected ILogger Logger { get; }

    protected TimeProvider Time { get; }

    /// <inheritdoc/>
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A cookie with no issue time is checked every time; one with an issue time, once the
        // interval has passed since it was issued or last renewed.
        DateTimeOffset? issued = context.Properties.IssuedUtc;

        if (issued is not null && Time.GetUtcNow() - issued.Value <= _options.ValidationInterval)
        {
            return;
        }

        HSUser? user = await VerifiedAccountAsync(context.Principal);

        if (user is null)
        {
            Logger.LogDebug("Security stamp validation failed; rejecting the cookie and ending the session.");

            context.RejectPrincipal();
            await SignIn.SignOutAsync(context.HttpContext);
            await context.HttpContext.SignOutAsync(IdentityConstants.TwoFactorRememberMeScheme);

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
