using System;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

using Homespool.Model.Entities;

namespace Homespool.Host.Middleware;

/// <summary>
/// When <see cref="SecurityOptions.RequireTwoFactor"/> is on, holds a signed-in account that has no
/// authenticator on the enrolment page until it has one.
/// </summary>
/// <remarks>
/// <para>
/// <b>It acts only on the application cookie</b>, which is what keeps it off everything that has no
/// second factor to offer. A printer authenticates with <c>PrusaConnect</c>, a script with
/// <c>ApiToken</c> or <c>X-Api-Key</c>; none of those is a person, and a path-prefix exemption list
/// would have to be kept in step with every route ever added. The scheme is the honest test.
/// </para>
/// <para>
/// <b>The account is asked on every request rather than cached in a claim.</b> A claim would be one
/// lookup per sign-in instead of one per request — and would be <i>wrong</i>: enrolling does not
/// refresh the cookie, so the claim would still say "no authenticator" and hold the account on the
/// enrolment page it had just completed. The alternative is adding <c>RefreshSignInAsync</c> to
/// <c>EnableAuthenticator</c> and depending on it staying there. At one-to-tens of users this is an
/// indexed lookup by primary key on requests that were already going to touch the database; if that
/// ever stops being true, the claim plus the refresh is the shape to reach for, together.
/// </para>
/// <para>
/// <b>Refusing rather than redirecting under <c>/api</c>.</b> A redirect to an HTML page is useless
/// to a script and arrives as a 200 — the reasoning <c>ApiStatusCodeCookieEvents</c> already applies
/// to the sign-in redirect, for the same caller.
/// </para>
/// </remarks>
public sealed class TwoFactorEnrolmentMiddleware
{
    /// <summary>Where an account without an authenticator is sent.</summary>
    private const string EnrolmentPath = "/Account/Manage/EnableAuthenticator";

    /// <summary>
    /// What such an account may still reach: the enrolment page and the codes it produces, the way
    /// out, and the sign-in pages it may bounce through.
    /// </summary>
    /// <remarks>
    /// <c>ShowRecoveryCodes</c> is not optional company for the enrolment page — it is the single
    /// render of a secret nothing can produce again, so shutting an account
    /// out of it would complete enrolment while withholding the codes.
    /// </remarks>
    private static readonly string[] Allowed =
    [
        EnrolmentPath,
        "/Account/Manage/ShowRecoveryCodes",
        "/Account/Logout",
        "/Account/Login",
        "/Account/LoginWith2fa",
        "/Account/LoginWithRecoveryCode",
        "/Account/AccessDenied",
        "/Account/Lockout",
    ];

    private readonly RequestDelegate _next;

    public TwoFactorEnrolmentMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context,
                                  UserManager<HSUser> userManager,
                                  IOptionsSnapshot<SecurityOptions> security)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(security);

        if (!security.Value.RequireTwoFactor || !SignedInInteractively(context))
        {
            await _next(context);

            return;
        }

        PathString path = context.Request.Path;

        if (Allowed.Any(allowed => path.StartsWithSegments(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);

            return;
        }

        HSUser? user = await userManager.GetUserAsync(context.User);

        // A cookie for an account that is gone is not this middleware's problem to answer; let the
        // request through and meet whatever already handles it.
        if (user is null || await userManager.GetTwoFactorEnabledAsync(user))
        {
            await _next(context);

            return;
        }

        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;

            return;
        }

        context.Response.Redirect(EnrolmentPath);
    }

    /// <summary>
    /// Whether this request is a person using the browser, as opposed to a printer or a script.
    /// </summary>
    private static bool SignedInInteractively(HttpContext context)
    {
        return context.User.Identity is { IsAuthenticated: true, AuthenticationType: not null }
               && string.Equals(context.User.Identity.AuthenticationType,
                                IdentityConstants.ApplicationScheme,
                                StringComparison.Ordinal);
    }
}
