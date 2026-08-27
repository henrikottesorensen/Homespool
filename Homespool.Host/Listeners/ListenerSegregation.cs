using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Homespool.Host.Listeners;

/// <summary>
/// Attaches a <see cref="ListenerRequirement"/> to every endpoint, derived from its route.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived rather than declared, because a declaration gets forgotten.</b> The spike behind this
/// design measured exactly that: with the constraint applied per route, a route added without it
/// appeared on <b>both</b> listeners, answering 200 on each, with no error and no warning — and it
/// will be forgotten by someone adding a <c>/p/</c> route while thinking about printers rather than
/// listeners. Keying on the prefix instead means a new <c>/p/</c> route is printer-only the moment it
/// exists.
/// </para>
/// <para>
/// That promotes Connect's own URL prefixes into the thing the boundary is built on: <c>/p/*</c> is
/// printer-authenticated (including <c>POST /p/camera</c>, which is a printer endpoint wearing a
/// camera name), and <c>/c/*</c> will be camera-authenticated.
/// </para>
/// <para>
/// The remaining way to lose the boundary is a <c>Map…</c> call that never passes through here, and
/// two things cover it. <see cref="ListenerSegregationMiddleware"/> falls back to the same rule
/// applied to the request path, so an unclassified route under <c>/p</c> is still printer-only rather
/// than served to everyone; and <c>RouteListenerSegregationTests</c> enumerates the application's real
/// endpoint list and fails if anything is unclassified at all.
/// </para>
/// <para>
/// The fallback is not belt-and-braces for its own sake: <c>MapStaticAssets</c> adds a file fallback
/// endpoint <i>outside</i> the builder it returns, so there is at least one endpoint no convention of
/// ours can reach, and refusing it outright would have meant 404ing static-file requests and logging a
/// warning on every one.
/// </para>
/// </remarks>
public static class ListenerSegregation
{
    /// <summary>
    /// Classifies every endpoint <paramref name="builder"/> produces.
    /// </summary>
    /// <typeparam name="TBuilder">The convention builder's own type, so mapping calls stay chainable.</typeparam>
    /// <param name="builder">The result of a <c>Map…</c> call.</param>
    /// <returns><paramref name="builder"/>.</returns>
    public static TBuilder SegregateByListener<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpoint =>
        {
            string? pattern = (endpoint as RouteEndpointBuilder)?.RoutePattern.RawText;

            endpoint.Metadata.Add(new ListenerRequirement(ClassFor(pattern)));
        });

        return builder;
    }

    /// <summary>
    /// The listener a route pattern belongs to.
    /// </summary>
    /// <param name="routePattern">
    /// The raw pattern, with or without a leading slash — attribute routes lose theirs, page routes
    /// never had one.
    /// </param>
    public static ListenerClass ClassFor(string? routePattern)
    {
        string path = (routePattern ?? string.Empty).TrimStart('/');

        // Segment-exact, so a page called "printers" or "profile" is not mistaken for the printer
        // protocol, nor "files" for the transfer one. Only "p" and "f", and things beneath them.
        if (IsPrefix(path, "p"))
        {
            return ListenerClass.Printer;
        }

        if (IsPrefix(path, "f"))
        {
            return ListenerClass.Transfer;
        }

        return ListenerClass.User;
    }

    private static bool IsPrefix(string path, string segment)
    {
        return path.Equals(segment, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase);
    }
}
