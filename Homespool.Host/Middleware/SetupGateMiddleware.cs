using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Homespool.Host.Services;

namespace Homespool.Host.Middleware;

/// <summary>
/// First-run gate: while no administrator exists, redirects human-facing pages to <c>/setup</c> so
/// the site cannot be navigated - Login, Register and everything else - until the bootstrap admin is
/// created. Becomes a no-op (one bool read) the moment setup completes.
/// </summary>
/// <remarks>
/// <para>
/// This is the counterpart to <see cref="Pages.SetupModel"/> 404ing once setup is complete: that stops
/// <c>/setup</c> being reused to mint a second admin; this stops the <i>rest</i> of the app being
/// used before the first one exists. Redirect rather than 404 so the operator is guided to the one
/// page that works.
/// </para>
/// <para>
/// Allowed through even before setup: <c>/setup</c> itself; the printer protocol under <c>/p</c>
/// and the encrypted download under <c>/f</c> (a firmware client must get its documented status
/// codes, never a 302 to an HTML page - and neither path is navigable or reveals anything, since
/// both answer only to a credential or a capability minted by this deployment); the
/// dev-only API docs (<c>/scalar</c>, <c>/openapi</c>); and any request for a static asset,
/// identified by a file extension - which covers the setup page's own CSS and JS regardless of
/// whether they are served from <c>wwwroot</c> or an <c>/Identity</c> path.
/// </para>
/// </remarks>
public sealed class SetupGateMiddleware : IMiddleware
{
    private readonly SetupState _setupState;

    public SetupGateMiddleware(SetupState setupState)
    {
        _setupState = setupState;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (_setupState.IsComplete || IsAllowedBeforeSetup(context.Request.Path))
        {
            await next(context);

            return;
        }

        context.Response.Redirect("/setup");
    }

    private static bool IsAllowedBeforeSetup(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        if (path.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/p", StringComparison.OrdinalIgnoreCase)

            // The transfer path, for the same reason as /p: firmware reads status codes and a 302 to
            // an HTML page is a failed transfer. It cannot be reached before setup in practice - a
            // transfer needs a printer, which needs an administrator to claim it - but "in practice"
            // is not what a gate should rest on, and the failure it would cause is silent.
            || path.StartsWithSegments("/f", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase)

            // Not navigable, and a container is never "not started yet" from a monitor's point of
            // view just because nobody has created the first administrator. Redirecting this to
            // /setup would make a fresh deployment answer probes with a 302 instead of its health -
            // and curl --fail treats a 302 as success, so it would report healthy while never
            // reaching the check.
            || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A file extension marks a static asset (.css, .js, .ico, fonts, ...). Page routes here carry
        // none, so this lets the setup page's own styling load while still gating every navigable page.
        return Path.HasExtension(path.Value);
    }
}
