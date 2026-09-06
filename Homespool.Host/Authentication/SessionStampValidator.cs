using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// The stamp check on the application cookie. A verified session gets a principal rebuilt from the
/// account - so a changed name, role or address shows up without signing in again - and its cookie
/// renewed; a stale one is ended.
/// </summary>
/// <remarks>
/// The rebuilt principal keeps the method and provider the session was signed in with, through
/// <see cref="LocalSignIn.RebuiltPrincipalAsync"/>. The framework's validator rebuilt from the
/// factory alone and noted, in a comment, that the authentication method was lost; a page that asks
/// how someone signed in should not get a different answer after half an hour.
/// </remarks>
public sealed class SessionStampValidator : StampValidator
{
    public SessionStampValidator(IOptions<SecurityStampValidatorOptions> options,
                                 IOptions<IdentityOptions> identity,
                                 UserManager<HSUser> users,
                                 LocalSignIn signIn,
                                 ILogger<SessionStampValidator> logger)
        : base(options, identity, users, signIn, logger)
    {
    }

    /// <inheritdoc/>
    protected override async Task<HSUser?> VerifiedAccountAsync(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        HSUser? user = await Users.GetUserAsync(principal);

        return user is not null && await StampMatchesAsync(user, principal) ? user : null;
    }

    /// <inheritdoc/>
    protected override async Task VerifiedAsync(HSUser user, CookieValidatePrincipalContext context)
    {
        context.ReplacePrincipal(await SignIn.RebuiltPrincipalAsync(user, context.Principal!));
        context.ShouldRenew = true;

        // On renewal without sliding expiration, the new ticket's length is measured from now so the
        // renewal does not extend the expiry.
        if (!context.Options.SlidingExpiration)
        {
            context.Properties.IssuedUtc = Time.GetUtcNow();
        }
    }
}
