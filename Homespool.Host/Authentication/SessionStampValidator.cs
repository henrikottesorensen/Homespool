using System;
using System.Buffers.Text;
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
/// The stamp check on the application cookie. A verified session gets a principal rebuilt from the
/// account - so a changed name, role or address shows up without signing in again - and its cookie
/// renewed; a stale one is ended.
/// </summary>
/// <remarks>
/// <para>
/// The rebuilt principal keeps the method, provider and passkey the session was signed in with,
/// through <see cref="LocalSignIn.RebuiltPrincipalAsync"/>. The framework's validator rebuilt from the
/// factory alone and noted, in a comment, that the authentication method was lost; a page that asks
/// how someone signed in should not get a different answer after half an hour.
/// </para>
/// <para>
/// <b>A session signed in with a passkey also needs that passkey to still be on the account.</b> A
/// passkey is revoked because the device it lives on is gone, and that device is usually signed in:
/// removing the credential without ending its session would leave the lost phone exactly as useful
/// as before. The stamp cannot do this - moving it signs out every browser the owner has, including
/// the one they are revoking from - so the session carries
/// <see cref="HSClaimTypes.PasskeyCredentialId"/> and is checked against the passkey itself. A
/// passkey session without that claim cannot be checked, and is ended rather than trusted.
/// </para>
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

        if (user is null || !await StampMatchesAsync(user, principal))
        {
            return null;
        }

        if (principal.HasClaim(JwtClaimTypes.AuthenticationMethod, PasskeyAuthenticationHandler.AuthenticationMethod)
            && !await PasskeyRemainsAsync(user, principal))
        {
            Logger.LogInformation("A session of user {UserId} was ended: the passkey it signed in with is no longer on the account.", user.Id);

            return null;
        }

        return user;
    }

    /// <summary>
    /// Whether the passkey <paramref name="principal"/> names is still one of <paramref name="user"/>'s.
    /// </summary>
    private async Task<bool> PasskeyRemainsAsync(HSUser user, ClaimsPrincipal principal)
    {
        string? encoded = principal.FindFirstValue(HSClaimTypes.PasskeyCredentialId);

        if (encoded is null)
        {
            return false;
        }

        byte[] credentialId;

        try
        {
            credentialId = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        return await Users.GetPasskeyAsync(user, credentialId) is not null;
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
