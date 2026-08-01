using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// Adds <see cref="HSUser.DisplayName"/> to the sign-in cookie, so the UI can greet someone without
/// a database round trip on every page render.
/// </summary>
/// <remarks>
/// <para>
/// The alternative was for <c>_LoginPartial</c> to call <c>GetUserAsync</c>, which is a query per
/// rendered page for the sake of one word in a header. A claim rides along in the cookie Identity
/// already issues, which is what this extension point is for.
/// </para>
/// <para>
/// <b>The cost is staleness</b>, and it is handled where the name changes: editing the display name
/// calls <c>RefreshSignInAsync</c>, re-issuing the cookie with the new value. Miss that and the
/// header keeps the old name until the next sign-in - so anything that writes
/// <see cref="HSUser.DisplayName"/> must refresh, exactly as <c>ConfirmEmailChange</c> already does
/// after changing the email.
/// </para>
/// </remarks>
public class HSUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<HSUser, IdentityRole<long>>
{
    /// <summary>The claim carrying <see cref="HSUser.DisplayName"/>.</summary>
    /// <remarks>
    /// Not <see cref="ClaimTypes.Name"/>: that one is the sign-in identity, populated by Identity
    /// itself from <c>UserName</c>, and overwriting it would change what
    /// <c>User.Identity.Name</c> and every authorization check see. This is a display concern and
    /// stays in its own claim.
    /// </remarks>
    public const string DisplayNameClaim = "homespool:display_name";

    public HSUserClaimsPrincipalFactory(UserManager<HSUser> userManager,
                                        RoleManager<IdentityRole<long>> roleManager,
                                        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    /// <inheritdoc/>
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(HSUser user)
    {
        ClaimsIdentity identity = await base.GenerateClaimsAsync(user);

        // Absent rather than empty when unset: a reader falls back to UserName, and an empty claim
        // would render an empty greeting instead.
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(DisplayNameClaim, user.DisplayName));
        }

        return identity;
    }
}
