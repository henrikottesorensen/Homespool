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
using Microsoft.Net.Http.Headers;

using Homespool.Host.Services;

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
/// forge it</b> - the <c>Sec-</c> prefix makes it a forbidden header name. So a write that carries it
/// is admitted only when the browser says <c>same-origin</c>, and any other value is believed.
/// </para>
/// <para>
/// <b>Absent is refused over HTTPS</b>, deliberately: there it means a browser too old to send the
/// header, which cannot then make the writes the site's own script makes (the WebRTC offer, ending a
/// live view), and that costs less than leaving the only script-forgeable alternative, a custom
/// header, as the floor. Every browser released since March 2023 sends it.
/// </para>
/// <para>
/// <b>Over plain HTTP absent is what every browser sends</b>: fetch metadata goes only to a
/// potentially trustworthy origin - HTTPS, or loopback - so a page at <c>http://192.168.1.10:8080</c>
/// sends none, on its own requests included. There the browser's <c>Origin</c> stands in, which is
/// forbidden to script too and which the site's own script sends on every write: it must be exactly
/// this request's own origin, built from <c>Host</c>. The browser writes that header and nothing here
/// rewrites it - <c>X-Forwarded-Host</c> is not honoured - and a sibling port or subdomain names
/// itself in <c>Origin</c>, so the gap <c>SameSite=Lax</c> leaves stays closed. A missing, <c>null</c>
/// or second <c>Origin</c> is refused. Whether the request is HTTPS is
/// <see cref="HttpRequest.IsHttps"/>, which behind the shipped proxy is the browser's own connection,
/// since that proxy's <c>X-Forwarded-Proto</c> is trusted. The fallback gives little away: over plain
/// HTTP the cookie itself crosses the network in clear.
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
/// exercise - so a page is left alone by construction. Reads are never refused, which holds only
/// while the API's GETs are reads: <c>Lax</c> still attaches the cookie to a GET a foreign page
/// navigates to, and to any GET from another origin on this site. So an action that reaches a
/// printer is a write whatever it returns - the storage listing is a POST for that reason.
/// Registered for every controller rather than declared per action, so a controller added later is
/// covered without anyone remembering to say so.
/// </para>
/// </remarks>
public sealed class SameOriginWriteFilter : IAsyncAuthorizationFilter
{
    /// <summary>The fetch-metadata header the browser sets and script cannot.</summary>
    public const string HeaderName = "Sec-Fetch-Site";

    /// <summary>The one value that admits a cookie-authenticated write.</summary>
    public const string SameOrigin = "same-origin";

    /// <summary>The longest value the header is defined to take is <c>same-origin</c>; more than this is not one.</summary>
    private const int MaxLoggedHeaderLength = 32;

    /// <summary>A host name is at most 253 characters; with a scheme and a port, more than this is not an origin.</summary>
    private const int MaxLoggedOriginLength = 280;

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
        StringValues origin = request.Headers[HeaderNames.Origin];

        if (!Refuses(request.Method, secFetchSite, byCookie.Succeeded, origin, PlainHttpOrigin(request)))
        {
            return;
        }

        // Information rather than Warning: on a healthy deployment this line means a browser old
        // enough not to send the header, or a page on another origin, and "the live view will not
        // start" is answered by it - the scheme says which of the two rules was applied. A browser
        // writes both headers itself; anything else holding the cookie writes what it likes, so the
        // values are cleaned and cut. The path needs neither - a PathString renders escaped.
        _logger.LogInformation(
            "Refused a cookie-authenticated {Method} to {Path} over {Scheme}: Sec-Fetch-Site is {SecFetchSite}, Origin is {Origin}.",
            request.Method,
            request.Path,
            request.IsHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            StringValues.IsNullOrEmpty(secFetchSite) ? "absent" : LogText.Clean(secFetchSite.ToString(), MaxLoggedHeaderLength),
            StringValues.IsNullOrEmpty(origin) ? "absent" : LogText.Clean(origin.ToString(), MaxLoggedOriginLength));

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
    /// <param name="origin">The <c>Origin</c> header, consulted only when <paramref name="secFetchSite"/> is absent.</param>
    /// <param name="plainHttpOrigin">
    /// This request's own origin when it arrived over plain HTTP, from <see cref="PlainHttpOrigin"/>;
    /// <see langword="null"/> over HTTPS, where <paramref name="origin"/> is never consulted.
    /// </param>
    public static bool Refuses(string method,
                               StringValues secFetchSite,
                               bool cookieAuthenticated,
                               StringValues origin = default,
                               string? plainHttpOrigin = null)
    {
        if (IsRead(method) || !cookieAuthenticated)
        {
            return false;
        }

        // Exactly one value, and that one. Two values is a request assembled by hand, not a browser.
        if (secFetchSite.Count != 0)
        {
            return secFetchSite.Count != 1 ||
                   !string.Equals(secFetchSite[0], SameOrigin, StringComparison.Ordinal);
        }

        // Scheme and host are case-insensitive, and the browser writes both headers from one URL.
        return plainHttpOrigin is null ||
               origin.Count != 1 ||
               !string.Equals(origin[0], plainHttpOrigin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The origin a page serving this request would have, as a browser writes it in <c>Origin</c> -
    /// or <see langword="null"/> when the request arrived over HTTPS or names no host.
    /// </summary>
    /// <param name="request">The request.</param>
    public static string? PlainHttpOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.IsHttps || !request.Host.HasValue)
        {
            return null;
        }

        return Uri.UriSchemeHttp + Uri.SchemeDelimiter + request.Host.ToUriComponent();
    }

    private static bool IsRead(string method)
    {
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
    }
}
