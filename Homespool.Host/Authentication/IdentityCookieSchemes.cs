// The four cookie schemes Identity's AddIdentity registers, transcribed from dotnet/aspnetcore at
// v10.0.11 (src/Identity/Core/src/IdentityCookiesBuilderExtensions.cs and
// IdentityServiceCollectionExtensions.cs). Copyright (c) .NET Foundation, MIT licence.

using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Duende.IdentityModel;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Authentication;

/// <summary>
/// The four cookie authentication schemes Identity's sign-in signs into and
/// reads back, registered here rather than inside the framework's <c>AddIdentity</c> so that every
/// handler this application authenticates with is declared in code it owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>The registrations are the framework's, line for line</b>, but for one event. Each method below is
/// the corresponding <c>Microsoft.AspNetCore.Identity</c> extension with nothing removed, and the
/// application cookie adds <c>OnCheckSlidingExpiration</c> so that its session row slides with it.
/// Anything this deployment wants different is set afterwards through <c>ConfigureApplicationCookie</c>
/// and its siblings, exactly as it was when the framework did the registering. <b>What the events resolve is
/// not the framework's any more</b>: the two validators are <see cref="SessionStampValidator"/>, which
/// holds the application cookie to a live session row on every request, and
/// <see cref="RememberedBrowserStampValidator"/>, which ends the session when the remembered-browser
/// cookie's stamp is stale as well. What the transcription buys is
/// that the scheme list, the cookie names, the lifetimes and the events are readable in one place
/// alongside the printer, token and OpenID Connect schemes - and that a fifth cookie scheme, when one
/// is needed, is added next to its four peers rather than bolted onto a black box.
/// </para>
/// <para>
/// <b>Only the application cookie is configured any further.</b> The external cookie carries a
/// provider's principal for the five minutes between callback and account decision; the two
/// two-factor cookies carry the pending sign-in and the remember-me decision. None of the three has a
/// login path or an API-facing shape, so none needs the treatment <c>ApiStatusCodeCookieEvents</c>
/// gives the application cookie.
/// </para>
/// </remarks>
public static class IdentityCookieSchemes
{
    /// <summary>
    /// Registers all four Identity cookie schemes, in the framework's order.
    /// </summary>
    public static AuthenticationBuilder AddIdentityCookieSchemes(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddApplicationCookieScheme()
                      .AddExternalCookieScheme()
                      .AddTwoFactorRememberMeCookieScheme()
                      .AddTwoFactorUserIdCookieScheme();
    }

    /// <summary>
    /// <see cref="IdentityConstants.ApplicationScheme"/>: the signed-in session. Every request checks it
    /// against its session row, which is what signs every other browser out after a password change,
    /// and the row's expiry moves when the cookie slides.
    /// </summary>
    /// <remarks>
    /// The cookie name is left at the handler's default, which derives it from the scheme name - the
    /// other three set it explicitly to the same effect. The login path set here is overwritten by
    /// <c>ConfigureApplicationCookie</c> in <c>Program</c>; it is kept so the transcription stays
    /// diffable against its source.
    /// </remarks>
    public static AuthenticationBuilder AddApplicationCookieScheme(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCookie(IdentityConstants.ApplicationScheme, options =>
        {
            options.LoginPath = new PathString("/Account/Login");
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync,

                // The one addition to the framework's registration: the session row follows the
                // cookie when it slides. See SessionStampValidator.
                OnCheckSlidingExpiration = SessionStampValidator.CheckSlidingExpirationAsync,
            };
        });
    }

    /// <summary>
    /// <see cref="IdentityConstants.ExternalScheme"/>: the principal an external provider handed back,
    /// held only until <c>Account/ExternalLogin</c> has decided whether it may become a session. The
    /// OpenID Connect handler signs into this scheme, never into the application cookie.
    /// </summary>
    public static AuthenticationBuilder AddExternalCookieScheme(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCookie(IdentityConstants.ExternalScheme, options =>
        {
            options.Cookie.Name = IdentityConstants.ExternalScheme;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
    }

    /// <summary>
    /// <see cref="IdentityConstants.TwoFactorRememberMeScheme"/>: the browser-level "don't ask again"
    /// decision after a second factor. Validated against the security stamp like the session cookie,
    /// through <see cref="ITwoFactorSecurityStampValidator"/>, so it too is revoked by a stamp change.
    /// </summary>
    public static AuthenticationBuilder AddTwoFactorRememberMeCookieScheme(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCookie(IdentityConstants.TwoFactorRememberMeScheme, options =>
        {
            options.Cookie.Name = IdentityConstants.TwoFactorRememberMeScheme;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = SecurityStampValidator.ValidateAsync<ITwoFactorSecurityStampValidator>,
            };
        });
    }

    /// <summary>
    /// <see cref="IdentityConstants.TwoFactorUserIdScheme"/>: which account passed its first factor and
    /// is waiting on its second, for the five minutes the second-factor page has to be answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five minutes from the first factor, and no longer.</b> The framework lets this cookie slide,
    /// so reading the second-factor page past the half-life reissued it for five more, and whoever held
    /// a phished password could keep a pending sign-in alive for as long as they refreshed. It does not
    /// slide here.
    /// </para>
    /// <para>
    /// <b>Checked against the account's security stamp on every read</b>
    /// (<see cref="ValidatePendingStampAsync"/>), which the framework does not do for this cookie. A new
    /// password, a reset authenticator or a removed login forgets a first factor passed before it.
    /// </para>
    /// <para>
    /// The return-URL redirect is disabled because nothing signs into this scheme through a challenge:
    /// <see cref="LocalSignIn"/> writes it directly on the way to the two-factor page, and a redirect
    /// issued by the handler would fight that navigation.
    /// </para>
    /// </remarks>
    public static AuthenticationBuilder AddTwoFactorUserIdCookieScheme(this AuthenticationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCookie(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.Name = IdentityConstants.TwoFactorUserIdScheme;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToReturnUrl = _ => Task.CompletedTask,
                OnValidatePrincipal = ValidatePendingStampAsync,
            };
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            options.SlidingExpiration = false;
        });
    }

    /// <summary>
    /// Rejects a pending sign-in whose account is gone or whose stamp has moved since the first factor
    /// passed, and forgets it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="StampValidator"/>, which ends the whole session on a mismatch. A browser can hold
    /// one account's session and another's pending sign-in, and the pending one going stale says
    /// nothing about the session - so only the pending cookie goes.
    /// </remarks>
    private static async Task ValidatePendingStampAsync(CookieValidatePrincipalContext context)
    {
        UserManager<HSUser> users = context.HttpContext.RequestServices.GetRequiredService<UserManager<HSUser>>();
        string? userId = context.Principal?.FindFirstValue(JwtClaimTypes.Subject);
        HSUser? user = userId is null ? null : await users.FindByIdAsync(userId);

        if (user is not null && await StampMatchesAsync(context, users, user))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
    }

    private static async Task<bool> StampMatchesAsync(CookieValidatePrincipalContext context, UserManager<HSUser> users, HSUser user)
    {
        if (!users.SupportsUserSecurityStamp)
        {
            return true;
        }

        string claimType = context.HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        string? claimed = context.Principal?.FindFirstValue(claimType);

        return claimed is not null && string.Equals(claimed, await users.GetSecurityStampAsync(user), StringComparison.Ordinal);
    }
}
