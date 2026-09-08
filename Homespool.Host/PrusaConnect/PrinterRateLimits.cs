using System;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Caps how fast the printer endpoints can be hit: a window per printer where the request names one,
/// under a ceiling on each route's total. These are the only routes an unauthenticated caller on the
/// internet can reach, and both registration verbs cost something: <c>POST /p/register</c> creates or
/// renews a database row per call, and <c>GET /p/register</c> is a guessing oracle for a pending
/// registration code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Assume the deployment is internet-facing.</b> People expose self-hosted printer servers
/// however firmly the documentation advises otherwise - OctoPrint's mass exposure is the
/// precedent - so "it is only on a LAN" is not a security property this project can rely on.
/// </para>
/// <para>
/// <b>Two limiters, and neither works without the other.</b> A window per printer stops one caller
/// spending everybody's allowance - which a single global window cannot, so one noisy or hostile
/// client could keep a whole fleet out. But the printer names itself with the <c>Fingerprint</c>
/// header, which is unauthenticated at this point in the pipeline and can be minted fresh per
/// request, so partitioning alone bounds each source and bounds the total at nothing. The ceiling is
/// what keeps the aggregate finite; the partition is what keeps one caller from consuming it.
/// </para>
/// <para>
/// <b>The ceiling is the <see cref="RateLimiterOptions.GlobalLimiter"/> so that it is acquired
/// first.</b> Measured, not assumed: with a ceiling of one permit and three requests carrying three
/// fingerprints, one request was admitted and the per-printer partition factory ran exactly once. A
/// request refused by the ceiling therefore mints no partition, which is what stops a caller
/// rotating fingerprints from turning the partition table into its own memory-growth vector - the
/// number of partitions a window can create is the ceiling. The same measurement showed the
/// partition callback running a second time for a refused request, so it must stay cheap and free of
/// side effects.
/// </para>
/// <para>
/// <b>What this does not fix.</b> A caller rotating fingerprints still spends the ceiling, so it can
/// still crowd out real printers on that route; what it can no longer do is exhaust the window from
/// one identity, and a printer that has its own window keeps it. Telling an enrolled printer from an
/// invented one needs identity this middleware does not have - it runs before authentication,
/// deliberately, so a rejected request costs no database work.
/// </para>
/// <para>
/// <b>Not partitioned per client IP</b>, and the reason is no longer the address itself. On the
/// shipped stack it is real by the time this runs - nginx sets <c>X-Real-IP</c> on the printer
/// server block, <c>XForwarded:KnownNetworks</c> names the proxy's subnet, and
/// <see cref="Listeners.ForwardedHeaderScope"/> honours that header on the printer listener because
/// <c>PrusaConnect:PrinterTls</c> puts the proxy in front of it. But with both <c>XForwarded</c> lists
/// empty, which is the default in code, no forwarded-headers middleware is registered at all and every
/// printer request behind a proxy carries the proxy's address. A per-IP partition would silently
/// collapse into one bucket for the world there, so the first brute-force attempt locks out the
/// household. The fingerprint has no such failure mode: it is either present and names one printer, or
/// absent, in which case every request lacking it shares one window. (Corrected 2026-09-02 - this used
/// to say that nothing here called <c>UseForwardedHeaders</c>, which stopped being true when that block
/// was added to the pipeline.)
/// </para>
/// <para>
/// <b>Limits are generous on purpose, because rejecting a real printer is expensive.</b> The
/// firmware treats any non-2xx from <c>/p/register</c> as <c>OnlineError::Server</c> and burns one
/// of only three POST retries before abandoning registration permanently (registrator.hpp,
/// <c>starting_retries = 3</c>); a rejected poll is milder but still noise. Every per-printer window
/// below is at least twice what the firmware's own cadence can spend, so the partition bites a
/// misbehaving client and never a working one.
/// </para>
/// <para>
/// The login form is <em>not</em> rate-limited here, and deliberately so: Identity's account
/// lockout now bounds password guessing per account (see <c>Login.cshtml.cs</c>), which is both
/// proxy-agnostic and impossible to evade by rotating source addresses. A global limiter on login
/// would instead let one attacker lock out every legitimate user at once.
/// </para>
/// <para>
/// <b>This class owns the one global limiter.</b> <c>PasskeyChallengeRateLimit</c> configures the
/// same options object and sets only a policy, so the ceiling here reaches no page: an endpoint
/// carrying any other policy, or none, is handed a partition with no limiter.
/// </para>
/// </remarks>
public static class PrinterRateLimits
{
    /// <summary>
    /// Rate-limit policy for <c>POST /p/register</c>, which asks for a registration code.
    /// </summary>
    /// <remarks>
    /// <b>Its own policy, and the one route with no per-printer window.</b> The printer sends no
    /// headers at all on this request - it names itself in the JSON body, which is unbound while this
    /// runs - so there is nothing to partition on without parsing an anonymous request's body ahead of
    /// any limit. Splitting it from <see cref="RegistrationPollPolicy"/> is what a separate window
    /// buys instead: the two halves cost different things and can no longer starve each other. This
    /// half is the one that writes a row, and it is now capped below what the shared window allowed.
    /// </remarks>
    public const string RegistrationStartPolicy = "printer-registration-start";

    /// <summary>
    /// Rate-limit policy for <c>GET /p/register</c>, which polls for the token behind a code.
    /// </summary>
    /// <remarks>
    /// Buddy's poll carries a <c>Code</c> header and nothing else, so this route has no per-printer
    /// window either. Partitioning on the code would give a guesser a fresh window per guess, which
    /// bounds nothing; the ceiling on this policy is what bounds the oracle, as it did before.
    /// </remarks>
    public const string RegistrationPollPolicy = "printer-registration-poll";

    /// <summary>Rate-limit policy for the <c>/p/ws</c> upgrade.</summary>
    public const string SocketPolicy = "printer-socket";

    /// <summary>
    /// Rate-limit policy for the pre-websocket HTTP transport - <c>POST /p/telemetry</c> and
    /// <c>POST /p/events</c>.
    /// </summary>
    /// <remarks>
    /// <b>Its own policy, because the traffic shape is the opposite of the socket's.</b> An upgrade
    /// happens once per connection, so <see cref="SocketPolicy"/>'s window covers a whole fleet;
    /// this transport posts roughly once a second <em>per printer</em>, so sharing that window would
    /// let two printers exhaust it and throttle every printer as a matter of course.
    /// </remarks>
    public const string HttpTransportPolicy = "printer-http-transport";

    /// <summary>
    /// Rate-limit policy for <c>GET /p/teams/{teamId}/files/{hash}/raw</c>, and the controller-wide
    /// default that every printer action inherits unless it names one of the policies above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Being the default is what this is for, more than the numbers are.</b> Every route on that
    /// controller reaches the printer authentication handler, which spends a PBKDF2 verifying the
    /// presented token before anything is authenticated - so an action shipped with no policy is an
    /// unmetered way to buy hashing at wire rate, whatever the action itself costs. The raw-fetch
    /// route was exactly that and nothing reported it, because a missing attribute looks like every
    /// other action nobody has annotated yet. As the controller's default, an action can only escape
    /// by writing <c>[DisableRateLimiting]</c>, which a reader sees.
    /// </para>
    /// <para>
    /// <b>Partitioned like the other two authenticated routes</b>, because the SDK sends its
    /// <c>Fingerprint</c> here: its download only attaches credentials when the URL starts with the
    /// server it posts telemetry to (<c>download.py</c>), which is what makes this route authenticated
    /// at all. So a printer collecting a file has its own window and cannot be starved by another.
    /// </para>
    /// </remarks>
    public const string FilePolicy = "printer-file";

    /// <summary>
    /// How many code requests every caller together may make in a <see cref="Window"/>. A printer
    /// POSTs about once in its life and gets three tries, so a fleet powering on for the first time
    /// stays far below this.
    /// </summary>
    public const int RegistrationStartCeiling = 120;

    /// <summary>
    /// How many registration polls every caller together may make in a <see cref="Window"/>. A
    /// printer polls every 5s, so ten of them sit near 120/minute under this.
    /// </summary>
    public const int RegistrationPollCeiling = 300;

    /// <summary>
    /// How many socket upgrades one printer may ask for in a <see cref="Window"/>. A printer holding
    /// a stale token retries roughly once a minute (observed), so this is twenty times what a real
    /// one spends.
    /// </summary>
    public const int SocketPerPrinterLimit = 20;

    /// <summary>How many socket upgrades every caller together may make in a <see cref="Window"/>.</summary>
    public const int SocketCeiling = 120;

    /// <summary>
    /// How many telemetry and event posts one printer may make in a <see cref="Window"/>. Firmware's
    /// HTTP transport posts telemetry every 1-4s and events on top, so one printer alone can spend
    /// about 90 a minute; this is twice that.
    /// </summary>
    public const int HttpTransportPerPrinterLimit = 180;

    /// <summary>
    /// How many telemetry and event posts every caller together may make in a <see cref="Window"/>.
    /// Sized for a ten-printer fleet with headroom.
    /// </summary>
    public const int HttpTransportCeiling = 1200;

    /// <summary>
    /// How many file fetches one printer may make in a <see cref="Window"/>. A fetch is one whole
    /// file, not one per chunk, and it happens when somebody sends a print - so a printer collecting
    /// twenty in a minute is already not a printer.
    /// </summary>
    public const int FilePerPrinterLimit = 20;

    /// <summary>
    /// How many file fetches every caller together may make in a <see cref="Window"/>. This is also
    /// the ceiling any future printer action inherits, so it is sized as a bound on an unmetered
    /// route rather than for the fetch alone.
    /// </summary>
    public const int FileCeiling = 120;

    /// <summary>The window every limit above is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The partition every request that names no printer shares. A caller may send this as its
    /// fingerprint and join that window, which costs it a window of its own and nobody else anything.
    /// </summary>
    private const string Unattributed = "(none)";

    /// <summary>
    /// Adds the rate limiter, the ceiling on each printer route, and the five printer policies.
    /// </summary>
    /// <remarks>
    /// <b>A policy name has to be added in both places, and neither omission is caught by anything
    /// that does not make a request.</b> Left out of the switch, it falls through to no limiter and
    /// the route keeps its per-printer window with no ceiling at all - which is the shape a caller
    /// rotating fingerprints walks straight through, silently and with ordinary answers. Left out of
    /// <c>AddPolicy</c>, every request to that route is a 500, thrown when the middleware fails to
    /// resolve the name - loud, but at request time rather than at startup, so a route no test
    /// exercises ships broken. Adding an arm and a policy together is the discipline; only a request
    /// can check it was kept.
    /// </remarks>
    public static IServiceCollection AddPrinterRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // The route's own name is the partition, so each policy gets its own ceiling and no
            // other endpoint in the application is limited here at all. Derived from the endpoint's
            // metadata rather than from the path, so the policy name stays the single thing that
            // decides which limits an action gets.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                string? policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

                return policy switch
                {
                    RegistrationStartPolicy => Ceiling(policy, RegistrationStartCeiling),
                    RegistrationPollPolicy => Ceiling(policy, RegistrationPollCeiling),
                    SocketPolicy => Ceiling(policy, SocketCeiling),
                    HttpTransportPolicy => Ceiling(policy, HttpTransportCeiling),
                    FilePolicy => Ceiling(policy, FileCeiling),
                    _ => RateLimitPartition.GetNoLimiter(string.Empty),
                };
            });

            // Neither registration verb names a printer this middleware can read, so both are the
            // ceiling and nothing else.
            options.AddPolicy(RegistrationStartPolicy, _ => RateLimitPartition.GetNoLimiter(string.Empty));
            options.AddPolicy(RegistrationPollPolicy, _ => RateLimitPartition.GetNoLimiter(string.Empty));

            options.AddPolicy(SocketPolicy, context => PerPrinter(context, SocketPerPrinterLimit));
            options.AddPolicy(HttpTransportPolicy, context => PerPrinter(context, HttpTransportPerPrinterLimit));
            options.AddPolicy(FilePolicy, context => PerPrinter(context, FilePerPrinterLimit));
        });

        return services;
    }

    /// <summary>
    /// One window for the whole of <paramref name="policy"/>, keyed on the policy's own name.
    /// </summary>
    private static RateLimitPartition<string> Ceiling(string policy, int permitLimit)
    {
        return RateLimitPartition.GetFixedWindowLimiter(policy, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0,
        });
    }

    /// <summary>
    /// One window per printer, keyed on the fingerprint the request carries.
    /// </summary>
    /// <remarks>
    /// Reduced through <see cref="PrinterFingerprint.Key"/>, which is both what identifies an
    /// enrolled credential and a bound on the key: a caller cannot choose a partition key longer than
    /// <see cref="PrinterFingerprint.KeyLength"/> characters.
    /// </remarks>
    private static RateLimitPartition<string> PerPrinter(HttpContext context, int permitLimit)
    {
        string printer = context.Request.Headers.TryGetValue(Headers.Fingerprint, out StringValues fingerprint)
                         && !StringValues.IsNullOrEmpty(fingerprint)
                             ? PrinterFingerprint.Key(fingerprint.ToString())
                             : Unattributed;

        return RateLimitPartition.GetFixedWindowLimiter(printer, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0,
        });
    }
}
