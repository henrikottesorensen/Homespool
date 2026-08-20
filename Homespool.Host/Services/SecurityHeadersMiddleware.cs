using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Services;

/// <summary>
/// Sets the three response headers that cost nothing and are absent by default: no MIME sniffing, no
/// framing, and no referrer leaving this origin.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a Content-Security-Policy</b>, beyond the one directive that carries no cost.
/// <c>frame-ancestors</c> is the exception: directives a policy does not name stay unrestricted, so
/// naming only that one restricts framing and nothing else. What still stands between here and a
/// real <c>script-src</c> is the colour-mode block in <c>_Layout</c>'s head, which is inline on
/// purpose so the theme is set before first paint and therefore needs a nonce, and one
/// <c>style="display: inline-block"</c> in <c>TwoFactorAuthentication</c>. Both are small; neither
/// is this change.
/// </para>
/// <para>
/// <b>Both <c>X-Frame-Options</c> and <c>frame-ancestors</c></b>, which is belt and braces on
/// purpose: the CSP directive supersedes the header and every current browser prefers it, while the
/// header is what an older one understands. Nothing in this application frames anything - checked,
/// there is no <c>iframe</c> in any page - so <c>DENY</c> costs nothing that is used.
/// </para>
/// <para>
/// <b><c>Referrer-Policy: same-origin</c> rather than the browser default.</b> The page makes no
/// third-party requests any more - the CDN script and stylesheet tags were the last of them, and
/// they are now served locally - so nothing routinely carries a referrer outward today. This is
/// about what leaves when something does: a link a user follows, an image someone pastes into a
/// print description, a future integration. The default,
/// <c>strict-origin-when-cross-origin</c>, hands this deployment's origin to whatever is on the
/// other end, and on a self-hosted box that origin is often a hostname describing a private
/// network. <c>same-origin</c> sends nothing outward while keeping full referrers internally.
/// </para>
/// <para>
/// <b>Applied to every listener, not only the user's.</b> A printer ignores all three, so scoping
/// this to the user port would buy nothing and would silently fail to cover a page served somewhere
/// new later. A rule with no exceptions is also a rule a test can state in one line.
/// </para>
/// <para>
/// Set on the way in, before <c>next</c>, because headers are committed the moment a response starts
/// writing. Setting them afterwards silently loses them on exactly the responses that have a body -
/// a rendered page, the health report - while still appearing to work on a bodyless 404 or 401,
/// whose headers are untouched when control comes back. That asymmetry is measured, not assumed:
/// with the write moved after <c>next</c>, the page and health cases fail and the 404 and 401 cases
/// pass.
/// </para>
/// <para>
/// HSTS is not here. It stays opt-in, which `internet-exposure.md` explains: the default deployment
/// serves a self-signed certificate, and pinning a browser to HTTPS against one is a way to lock
/// somebody out of their own printer.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        IHeaderDictionary headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy = "frame-ancestors 'none'";

        // By name: IHeaderDictionary types the other three but not this one, and Referer - the
        // request header it is easy to reach for instead - is a different header entirely.
        headers["Referrer-Policy"] = "same-origin";

        await next(context);
    }
}
