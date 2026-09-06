using System.Security.Claims;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// The stamp check on the remembered-browser cookie: the account it names, by
/// <see cref="JwtClaimTypes.Subject"/>, must still carry the stamp the cookie was written with. A
/// verified cookie is left as it is; a stale one ends the session along with itself.
/// </summary>
/// <remarks>
/// Transcribed from the framework's <c>TwoFactorSecurityStampValidator&lt;TUser&gt;</c> at v10.0.11,
/// which found the account through <c>Identity.Name</c> because its cookie carried the id as
/// <c>ClaimTypes.Name</c>; <see cref="LocalSignIn.RememberClientAsync"/> writes <c>sub</c>.
/// </remarks>
public sealed class RememberedBrowserStampValidator : StampValidator, ITwoFactorSecurityStampValidator
{
    public RememberedBrowserStampValidator(IOptions<SecurityStampValidatorOptions> options,
                                           IOptions<IdentityOptions> identity,
                                           UserManager<HSUser> users,
                                           LocalSignIn signIn,
                                           ILogger<RememberedBrowserStampValidator> logger)
        : base(options, identity, users, signIn, logger)
    {
    }

    /// <inheritdoc/>
    protected override async Task<HSUser?> VerifiedAccountAsync(ClaimsPrincipal? principal)
    {
        string? userId = principal?.FindFirstValue(JwtClaimTypes.Subject);

        if (userId is null)
        {
            return null;
        }

        HSUser? user = await Users.FindByIdAsync(userId);

        return user is not null && await StampMatchesAsync(user, principal!) ? user : null;
    }

    /// <inheritdoc/>
    protected override Task VerifiedAsync(HSUser user, CookieValidatePrincipalContext context)
    {
        return Task.CompletedTask;
    }
}
