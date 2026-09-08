using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Homespool.Host.Middleware;

/// <summary>
/// Sets the response headers that cost nothing and are absent by default: no MIME sniffing, no
/// framing, no referrer leaving this origin, and a script policy that admits only this origin's own
/// files and the one inline block that carries this response's nonce.
/// </summary>
/// <remarks>
/// <para>
/// <b>The policy is about scripts, and it is the layer behind every encoding rule.</b>
/// <c>script-src 'self' 'nonce-…'</c> means a browser runs script from this origin's own files and
/// from an inline block that carries the nonce minted for this response, and nothing else: not an
/// inline block without one, not an <c>onclick</c> attribute, not a <c>javascript:</c> link, not a
/// script tag somebody managed to write into a page. Every value the pages render is encoded, and a
/// test scans every <c>Html.Raw</c> for the exceptions - but the next mistake in that family, whenever
/// it comes, now produces a console violation rather than code running as the signed-in user. The
/// same test file that scans <c>Html.Raw</c> has a sibling that refuses an inline script without a
/// nonce and any inline handler, so the policy cannot be quietly broken by a new view either.
/// </para>
/// <para>
/// <b>Only one inline block exists, and it is inline on purpose.</b> The colour-mode script in
/// <c>_Layout</c>'s head sets the theme before first paint; loaded from a file it would run after
/// the first frame and the page would flash. It carries the nonce, which <see cref="CspNonce"/>
/// mints once per request and both this middleware and the view read from the same scoped
/// instance. Every other script is a local file under <c>wwwroot</c>, so <c>'self'</c> covers it.
/// </para>
/// <para>
/// <b>What the policy deliberately does not name.</b> <c>style-src</c> is unset: Bootstrap sets
/// inline styles from script and a handful of views carry a <c>style</c> attribute, and none of
/// that is the attack surface a script policy exists for. <c>connect-src</c> is unset: the pages
/// fetch only this origin, and WebRTC media is not governed by it anyway. <c>form-action</c> is
/// unset: the identity-provider sign-in leaves this origin by redirect and the directive's
/// treatment of that varies by browser. <c>object-src 'none'</c> and <c>base-uri 'self'</c> are
/// named because each closes a way around <c>script-src</c> at no cost - a plugin embed, or a
/// <c>base</c> tag that re-points every relative script URL.
/// </para>
/// <para>
/// <b>Swagger UI in Development is the one carve-out.</b> It serves its own page with inline
/// scripts that carry no nonce, so under <c>/swagger</c>, and only when the environment is
/// Development, the policy names framing alone. Production has no Swagger, so production has no
/// carve-out.
/// </para>
/// <para>
/// <b>Both <c>X-Frame-Options</c> and <c>frame-ancestors</c></b>, which is belt and braces on
/// purpose: the CSP directive supersedes the header and every current browser prefers it, while the
/// header is what an older one understands. Nothing in this application frames anything - checked,
/// there is no <c>iframe</c> in any page - so <c>DENY</c> costs nothing that is used.
/// </para>
/// <para>
/// <b><c>Referrer-Policy: same-origin</c> rather than the browser default.</b> The page makes no
/// third-party requests, so nothing routinely carries a referrer outward today. This is about what
/// leaves when something does: a link a user follows, an image someone pastes into a print
/// description, a future integration. The default, <c>strict-origin-when-cross-origin</c>, hands this
/// deployment's origin to whatever is on the other end, and on a self-hosted box that origin is
/// often a hostname describing a private network. <c>same-origin</c> sends nothing outward while
/// keeping full referrers internally.
/// </para>
/// <para>
/// <b>Applied to every listener, not only the user's.</b> A printer ignores all of them, so scoping
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
/// HSTS is not here. It is a deployment setting the proxy applies only to names served with an
/// issued certificate, because pinning a browser to HTTPS against a self-signed one is a way to lock
/// somebody out of their own printer.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware : IMiddleware
{
    /// <summary>The directives that hold whatever the path; the script policy is added in front.</summary>
    private const string FramingAndBase = "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    private readonly IHostEnvironment _environment;

    public SecurityHeadersMiddleware(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IHeaderDictionary headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy = Policy(context);

        // By name: IHeaderDictionary types the other three but not this one, and Referer - the
        // request header it is easy to reach for instead - is a different header entirely.
        headers["Referrer-Policy"] = "same-origin";

        await next(context);
    }

    private string Policy(HttpContext context)
    {
        if (_environment.IsDevelopment()
            && context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            return FramingAndBase;
        }

        string nonce = context.RequestServices.GetRequiredService<CspNonce>().Value;

        return $"script-src 'self' 'nonce-{nonce}'; {FramingAndBase}";
    }
}
