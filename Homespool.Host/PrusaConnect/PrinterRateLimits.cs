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
/// <b>Deliberately global, not partitioned per client IP</b>, and the first reason is the aggregate
/// rather than the address: a per-IP window bounds each source and bounds the total at nothing, where
/// one window keeps the ~430k/day ceiling below. That is the argument
/// <see cref="HttpTransportPolicy"/> makes about the fingerprint header, and it holds however
/// trustworthy the address is.
/// </para>
/// <para>
/// <b>The second reason is that the address is only trustworthy on a configured deployment.</b> On
/// the shipped stack it is real by the time this runs - nginx sets <c>X-Real-IP</c> on the printer
/// server block, <c>XForwarded:KnownNetworks</c> names the proxy's subnet, and
/// <see cref="Listeners.ForwardedHeaderScope"/> honours that header on the printer listener because
/// <c>PrusaConnect:PrinterTls</c> puts the proxy in front of it. But with both <c>XForwarded</c> lists
/// empty, which is the default in code, no forwarded-headers middleware is registered at all and every
/// printer request behind a proxy carries the proxy's address. A per-IP partition would silently
/// collapse into one bucket for the world there, so the first brute-force attempt locks out the
/// household: strictly worse than no limiting at all. Per-IP is something to add beside this window,
/// not instead of it. (Corrected 2026-09-02 - this used to say that nothing here called
/// <c>UseForwardedHeaders</c>, which stopped being true when that block was added to the pipeline.)
/// </para>
/// <para>
/// <b>Limits are generous on purpose, because rejecting a real printer is expensive.</b> The
/// firmware treats any non-2xx from <c>/p/register</c> as <c>OnlineError::Server</c> and burns one
/// of only three POST retries before abandoning registration permanently (registrator.hpp,
/// <c>starting_retries = 3</c>); a rejected poll is milder but still noise. A healthy printer
/// POSTs about once in its life and polls every 5s (≈12/min), so ten printers sit near 120/min
/// against the 300/min ceiling here, while an attacker is bounded to ~430k attempts/day instead of
/// unbounded. That is not the whole answer for the code-guessing surface - a per-registration
/// attempt cap is - but it turns "unlimited" into "bounded".
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
