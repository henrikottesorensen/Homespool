using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Homespool.Host.Authorisation;

/// <summary>
/// Refuses a write to a controller when the sign-in cookie authenticated the caller and the browser
/// does not say the request came from this origin.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cookie is the one credential a foreign page can spend without holding it.</b> A bearer
/// token or an API key has to be placed in a header by whoever holds it, so a page on another site
/// cannot make the browser attach one. The sign-in cookie is attached by the browser itself, which
/// makes every cookie-authenticated write on <c>/api/v1</c> a cross-site request forgery target.
/// Razor pages carry an antiforgery token for exactly this; the controllers carry none, and were
/// protected by the cookie's <c>SameSite=Lax</c> alone - which withholds it on a cross-<em>site</em>
/// request and says nothing about a cross-origin request from the same site: a sibling subdomain, or
/// another service on this host name at another port.
/// </para>
/// <para>
/// <b><c>Sec-Fetch-Site</c> is the browser's own word on where a request came from, and script cannot
/// forge it</b> - the <c>Sec-</c> prefix makes it a forbidden header name. So a write is admitted only
/// when the browser says <c>same-origin</c>. <b>Absent is refused too</b>, deliberately: a browser too
/// old to send the header cannot make the one write the site's own script makes (the WebRTC offer),
/// and that costs less than leaving the only script-forgeable alternative, a custom header, as the
/// floor. Every browser released since March 2023 sends it.
/// </para>
/// <para>
/// <b>Whether the cookie authenticated the request is asked of the cookie scheme, not read off the
/// principal.</b> The token handlers build their principal through the same claims factory the cookie
/// does, so a token-authenticated identity carries the cookie scheme's name too; nothing on the
/// principal tells the two apart. Asking
/// <see cref="AuthenticationHttpContextExtensions.AuthenticateAsync(HttpContext, string)"/> for the
/// one scheme does, and the cookie handler has already done the work once for this request, so the
/// second ask is served from its cache. A request authenticated by a token alone has no ambient credential and is never
/// refused here, so scripts and PrusaSlicer are untouched; the printer scheme likewise.
/// </para>
/// <para>
/// <b>Controllers only, and only their writes.</b> <see cref="MvcOptions.Filters"/> reaches Razor
/// Pages as well, and every page handler that writes already validates the antiforgery token, which
/// is a stronger proof of origin than this and one the page tests sign in without this header to
/// exercise - so a page is left alone by construction. Reads are never refused: a cross-site GET
/// carries no cookie under <c>Lax</c>, and every API GET is a read. Registered for every controller
/// rather than declared per action, so a controller added later is covered without anyone remembering
/// to say so.
/// </para>
/// </remarks>
public sealed class SameOriginWriteFilter : IAsyncAuthorizationFilter
{
    /// <summary>The fetch-metadata header the browser sets and script cannot.</summary>
    public const string HeaderName = "Sec-Fetch-Site";

    /// <summary>The one value that admits a cookie-authenticated write.</summary>
    public const string SameOrigin = "same-origin";

    private readonly ILogger<SameOriginWriteFilter> _logger;

    public SameOriginWriteFilter(ILogger<SameOriginWriteFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActionDescriptor is not ControllerActionDescriptor)
        {
            return;
        }

        HttpRequest request = context.HttpContext.Request;

        if (IsRead(request.Method))
        {
            return;
        }

        AuthenticateResult byCookie = await context.HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        StringValues secFetchSite = request.Headers[HeaderName];

        if (!Refuses(request.Method, secFetchSite, byCookie.Succeeded))
        {
            return;
        }

        // Information rather than Warning: on a healthy deployment this line means a browser old
        // enough not to send the header, and "the live view will not start" is answered by it.
        _logger.LogInformation("Refused a cookie-authenticated {Method} to {Path}: Sec-Fetch-Site is {SecFetchSite}.",
                               request.Method,
                               request.Path,
                               StringValues.IsNullOrEmpty(secFetchSite) ? "absent" : secFetchSite.ToString());

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Not permitted from this origin.",
            Detail = "A write signed in by the browser's cookie must come from this site's own pages.",
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    /// <summary>
    /// Whether a request is refused: a write, authenticated by the cookie, that the browser does not
    /// attribute to this origin.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="secFetchSite">The <c>Sec-Fetch-Site</c> header, however many values it carries.</param>
    /// <param name="cookieAuthenticated">Whether the sign-in cookie authenticated this request.</param>
    public static bool Refuses(string method, StringValues secFetchSite, bool cookieAuthenticated)
    {
        if (IsRead(method) || !cookieAuthenticated)
        {
            return false;
        }

        // Exactly one value, and that one. Two values is a request assembled by hand, not a browser.
        return secFetchSite.Count != 1
               || !string.Equals(secFetchSite[0], SameOrigin, StringComparison.Ordinal);
    }

    private static bool IsRead(string method)
    {
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
    }
}
