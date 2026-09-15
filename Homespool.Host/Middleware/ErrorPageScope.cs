using System;

using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Middleware;

/// <summary>
/// Which requests a failure answers with the error page, rather than with a bare 500.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page is for a person at a browser, so everything that is not one keeps the bare 500.</b>
/// Firmware on <c>/p</c> and <c>/f</c> reads status codes and nothing else; a script on <c>/api</c>,
/// PrusaSlicer on <c>/compat</c> and a monitor on <c>/health</c> would each be handed a page of HTML
/// they cannot use, in place of the empty body they can.
/// </para>
/// <para>
/// <b>Keyed on the path, because the handler sits before routing</b> and there is no endpoint to ask
/// yet. The prefixes are the ones <see cref="Listeners.ListenerSegregation"/> classifies by, plus the
/// three machine-facing prefixes on the user listener.
/// </para>
/// </remarks>
public static class ErrorPageScope
{
    /// <summary>Where the error page is served, and what the exception handler re-runs the request as.</summary>
    public const string Path = "/Error";

    private static readonly string[] MachinePrefixes = ["/p", "/f", "/api", "/compat", "/health"];

    /// <summary>Whether a failure on <paramref name="context"/>'s request is answered with the error page.</summary>
    public static bool Covers(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PathString path = context.Request.Path;

        foreach (string prefix in MachinePrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
