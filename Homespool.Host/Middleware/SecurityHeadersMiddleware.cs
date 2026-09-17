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
/// source test refuses every way of writing a string as markup - but the next mistake in that family,
/// whenever it comes, now produces a console violation rather than code running as the signed-in user.
/// A sibling test refuses an inline script without a nonce and any inline handler, so the policy
/// cannot be quietly broken by a new view either.
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
/// <b>Two carve-outs, both Development only.</b> Swagger UI and the framework's developer exception
/// page each serve their own inline scripts with no nonce, so under <c>/swagger</c>, and on a 500,
/// the policy names framing and base alone. The 500 is how the exception page is recognised: it
/// leaves nothing else on the request to tell it apart, and in Development nothing else answers
/// 500 with a page of ours. Production has neither Swagger nor that page, so production has no
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
/// Written as the response starts, from a callback registered on the way in, because headers are
/// committed the moment a response starts writing. Setting them after <c>next</c> returns silently
/// loses them on exactly the responses that have a body - a rendered page, the health report - while
/// still appearing to work on a bodyless 404 or 401, whose headers are untouched when control comes
/// back. That asymmetry is measured, not assumed: with the write moved after <c>next</c>, the page and
/// health cases fail and the 404 and 401 cases pass.
/// </para>
/// <para>
/// <b>Not written before <c>next</c> either, though that would reach every response.</b> An
/// exception handler clears the response before it re-runs the pipeline for its error page, headers
/// included, and re-runs only what was registered after it - so headers written before <c>next</c>
/// would be missing from the error page, while a starting callback survives the clear. Measured with
/// the framework's handler: an eagerly written header absent from the error response, a callback's
/// header present.
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

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // On the way in, not in the callback: an error page re-run changes the request's path while
        // it renders, and the path this answers for is the one that arrived.
        bool swagger = _environment.IsDevelopment() &&
                       context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase);

        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;

            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = Policy(context, swagger);

            // By name: IHeaderDictionary types the other three but not this one, and Referer - the
            // request header it is easy to reach for instead - is a different header entirely.
            headers["Referrer-Policy"] = "same-origin";

            return Task.CompletedTask;
        });

        return next(context);
    }

    private string Policy(HttpContext context, bool swagger)
    {
        if (swagger ||
            (_environment.IsDevelopment() && context.Response.StatusCode == StatusCodes.Status500InternalServerError))
        {
            return FramingAndBase;
        }

        string nonce = context.RequestServices.GetRequiredService<CspNonce>().Value;

        return $"script-src 'self' 'nonce-{nonce}'; {FramingAndBase}";
    }
}
