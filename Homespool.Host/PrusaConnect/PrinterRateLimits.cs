using System;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Caps how fast the anonymous printer endpoints can be hit. These are the only routes an
/// unauthenticated caller on the internet can reach, and both cost something: <c>POST
/// /p/register</c> creates or renews a database row per call, and <c>GET /p/register</c> is a
/// guessing oracle for a pending registration code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Assume the deployment is internet-facing.</b> People expose self-hosted printer servers
/// however firmly the documentation advises otherwise - OctoPrint's mass exposure is the
/// precedent - so "it is only on a LAN" is not a security property this project can rely on.
/// </para>
/// <para>
/// <b>Deliberately global, not partitioned per client IP.</b> The documented way to expose this
/// service is behind a reverse proxy (<c>PRINTER_HOST</c>/<c>PRINTER_TLS</c>), and nothing here
/// calls <c>UseForwardedHeaders</c> - so every request's <c>RemoteIpAddress</c> would be the
/// proxy's. Partitioning on that puts every printer and every attacker in one bucket, meaning the
/// first brute-force attempt locks out the household: strictly worse than no limiting at all.
/// Honouring <c>X-Forwarded-For</c> is its own piece of work (it needs
/// <c>KnownProxies</c>/<c>KnownNetworks</c>, or an attacker simply rotates the header for
/// unlimited buckets), and per-IP limits should wait for it.
/// </para>
/// <para>
/// The login form is <em>not</em> rate-limited here, and deliberately so: Identity's account
/// lockout now bounds password guessing per account (see <c>Login.cshtml.cs</c>), which is both
/// proxy-agnostic and impossible to evade by rotating source addresses. A global limiter on login
/// would instead let one attacker lock out every legitimate user at once.
/// </para>
/// </remarks>
public static class PrinterRateLimits
{
    /// <summary>
    /// Rate-limit policy for the two anonymous <c>/p/register</c> actions. Named here so the policy
    /// and the <c>[EnableRateLimiting]</c> attributes on the controller cannot drift apart.
    /// </summary>
    public const string RegistrationPolicy = "printer-registration";

    /// <summary>Rate-limit policy for the <c>/p/ws</c> upgrade.</summary>
    public const string SocketPolicy = "printer-socket";

    /// <summary>
    /// Rate-limit policy for the pre-websocket HTTP transport - <c>POST /p/telemetry</c> and
    /// <c>POST /p/events</c>.
    /// </summary>
    /// <remarks>
    /// <b>Its own policy, because the traffic shape is the opposite of the socket's.</b> An upgrade
    /// happens once per connection, so <see cref="SocketPolicy"/>'s 120/minute covers a whole fleet;
    /// this transport posts roughly once a second <em>per printer</em>, so sharing that window would
    /// let two printers exhaust it and throttle every printer as a matter of course.
    /// </remarks>
    public const string HttpTransportPolicy = "printer-http-transport";

    /// <summary>
    /// Adds the rate limiter and the three printer policies.
    /// </summary>
    public static IServiceCollection AddPrinterRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddFixedWindowLimiter(RegistrationPolicy, limiter =>
            {
                limiter.PermitLimit = 300;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });

            options.AddFixedWindowLimiter(SocketPolicy, limiter =>
            {
                // A printer holding a stale token retries roughly once a minute (observed), so
                // this is ample for a fleet while still
                // bounding an attacker probing tokens.
                limiter.PermitLimit = 120;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });

            options.AddFixedWindowLimiter(HttpTransportPolicy, limiter =>
            {
                // Sized for the wire rather than for a connection attempt: firmware's HTTP transport
                // posts telemetry every 1-4s per printer and events on top, so one printer alone can
                // spend ~90/minute. This covers a ten-printer fleet with headroom.
                //
                // Global rather than per printer, and that is a limitation rather than a choice: this
                // middleware runs before UseAuthentication (deliberately, so a rejected request costs
                // no database work), so no identity is resolved when the partition key is computed.
                // The Fingerprint header is the only pre-auth identity available and an attacker can
                // mint a fresh one per request, which buys isolation between honest printers at the
                // cost of an unbounded aggregate - the opposite of what the threat model asks
                // for. One window for the transport keeps that ceiling.
                limiter.PermitLimit = 1200;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });

        return services;
    }
}
