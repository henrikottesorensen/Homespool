using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

using Homespool.Host.RateLimiting;

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
    /// How many code requests every caller together may make in a <see cref="Window"/>. A printer
    /// POSTs about once in its life and gets three tries, so a fleet powering on for the first time
    /// stays far below this.
    /// </summary>
    /// <remarks>
    /// <b><see cref="RateLimitPolicies.PrinterRegistrationStart"/> is its own policy, and one of the two
    /// routes with no per-printer window.</b> The printer sends no headers at all on this request - it
    /// names itself in the JSON body, which is unbound while this runs - so there is nothing to
    /// partition on without parsing an anonymous request's body ahead of any limit. Splitting it from
    /// the poll is what a separate window buys instead: the two halves cost different things and can no
    /// longer starve each other. This half is the one that writes a row, and it is capped below what
    /// the shared window allowed.
    /// </remarks>
    public const int RegistrationStartCeiling = 120;

    /// <summary>
    /// How many registration polls every caller together may make in a <see cref="Window"/>. A
    /// printer polls every 5s, so ten of them sit near 120/minute under this.
    /// </summary>
    /// <remarks>
    /// Buddy's poll carries a <c>Code</c> header and nothing else, so
    /// <see cref="RateLimitPolicies.PrinterRegistrationPoll"/> has no per-printer window either.
    /// Partitioning on the code would give a guesser a fresh window per guess, which bounds nothing;
    /// this ceiling is what bounds the oracle.
    /// </remarks>
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
    /// <remarks>
    /// <b><see cref="RateLimitPolicies.PrinterHttpTransport"/> is its own policy, because the traffic
    /// shape is the opposite of the socket's.</b> An upgrade happens once per connection, so
    /// <see cref="RateLimitPolicies.PrinterSocket"/>'s window covers a whole fleet; this transport
    /// posts roughly once a second <em>per printer</em>, so sharing that window would let two printers
    /// exhaust it and throttle every printer as a matter of course.
    /// </remarks>
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
    /// <remarks>
    /// <b>Partitioned like the other two authenticated routes</b>, because the SDK sends its
    /// <c>Fingerprint</c> here: its download only attaches credentials when the URL starts with the
    /// server it posts telemetry to (<c>download.py</c>), which is what makes this route authenticated
    /// at all. So a printer collecting a file has its own window and cannot be starved by another.
    /// </remarks>
    public const int FilePerPrinterLimit = 20;

    /// <summary>
    /// How many file fetches every caller together may make in a <see cref="Window"/>. This is also
    /// the ceiling any future printer action inherits, so it is sized as a bound on an unmetered
    /// route rather than for the fetch alone.
    /// </summary>
    /// <remarks>
    /// <b>Being the controller-wide default is what <see cref="RateLimitPolicies.PrinterFile"/> is for,
    /// more than the numbers are.</b> Every route on that controller reaches the printer authentication
    /// handler, which spends a PBKDF2 verifying the presented token before anything is authenticated -
    /// so an action shipped with no policy is an unmetered way to buy hashing at wire rate, whatever
    /// the action itself costs. The raw-fetch route was exactly that and nothing reported it, because a
    /// missing attribute looks like every other action nobody has annotated yet. As the controller's
    /// default, an action can only escape by writing <c>[DisableRateLimiting]</c>, which a reader sees.
    /// </remarks>
    public const int FileCeiling = 120;

    /// <summary>The window every limit above is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The partition every request that names no printer shares. A caller may send this as its
    /// fingerprint and join that window, which costs it a window of its own and nobody else anything.
    /// </summary>
    private const string Unattributed = "(none)";

    /// <summary>
    /// Every policy this class wires, and both of the limits each one gets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One table, because a policy needs registering in two places and the pairing used to be a
    /// habit.</b> The ceiling and the per-printer window are separate mechanisms - a
    /// <see cref="RateLimiterOptions.GlobalLimiter"/> partition and an endpoint policy - keyed on the
    /// same string, and neither omission announced itself. Wiring one and not the other left a route
    /// with a per-printer window and <em>no ceiling at all</em>, answering normally throughout, which
    /// is precisely what a caller minting a fresh fingerprint per request walks through; the reverse
    /// answered 500 on every request, at request time rather than at startup, so a route no test drove
    /// would have shipped broken. Both were reachable by forgetting one line. Driving both
    /// registrations from this table is what makes the halves impossible to separate.
    /// </para>
    /// <para>
    /// <b>A null <see cref="PolicyLimits.PerPrinter"/> means the ceiling and nothing else</b>, which
    /// is the honest shape for the two registration verbs: neither names a printer this middleware can
    /// read, so there is nothing to partition on. It is a deliberate value here rather than an absent
    /// entry, so the table lists every policy and a reader can see which ones have no window and why.
    /// </para>
    /// <para>
    /// Frozen because it is read on the global limiter's per-request path - which the measurement in
    /// this class's own remarks shows running a second time for a refused request - and written once.
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<string, PolicyLimits> Policies =
        new Dictionary<string, PolicyLimits>(StringComparer.Ordinal)
        {
            [RateLimitPolicies.PrinterRegistrationStart] = new(RegistrationStartCeiling, null),
            [RateLimitPolicies.PrinterRegistrationPoll] = new(RegistrationPollCeiling, null),
            [RateLimitPolicies.PrinterSocket] = new(SocketCeiling, SocketPerPrinterLimit),
            [RateLimitPolicies.PrinterHttpTransport] = new(HttpTransportCeiling, HttpTransportPerPrinterLimit),
            [RateLimitPolicies.PrinterFile] = new(FileCeiling, FilePerPrinterLimit),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The policies this class wires, for a test that has to ask what an endpoint may legitimately
    /// name. A policy an endpoint carries that is absent here is wired nowhere.
    /// </summary>
    public static IReadOnlyCollection<string> PolicyNames => Policies.Keys;

    /// <summary>
    /// Adds the rate limiter and, from <see cref="Policies"/>, both halves of every printer policy.
    /// </summary>
    /// <remarks>
    /// Neither half is written out per policy here, deliberately: they are the two registrations that
    /// have to agree, so they are made from one entry in one loop. See <see cref="Policies"/> for what
    /// forgetting one used to cost.
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

                // An endpoint carrying no policy, or one this class does not wire, is not limited
                // here - which is what keeps the ceiling off every page in the application.
                return policy is not null && Policies.TryGetValue(policy, out PolicyLimits limits)
                           ? Ceiling(policy, limits.Ceiling)
                           : RateLimitPartition.GetNoLimiter(string.Empty);
            });

            foreach (KeyValuePair<string, PolicyLimits> entry in Policies)
            {
                // Captured per iteration, and read inside the callback rather than branched on out
                // here, so one lambda serves both shapes: a policy with no per-printer window is the
                // ceiling and nothing else.
                int? perPrinter = entry.Value.PerPrinter;

                options.AddPolicy(entry.Key, context => perPrinter is { } permitLimit
                                                            ? PerPrinter(context, permitLimit)
                                                            : RateLimitPartition.GetNoLimiter(string.Empty));
            }
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

    /// <summary>
    /// What one policy is allowed: a ceiling on the route's total, and a window per printer where the
    /// request names one.
    /// </summary>
    /// <param name="Ceiling">
    /// Permits every caller together may spend on this policy's route in a <see cref="Window"/>.
    /// Required, because a route with no ceiling is bounded only per fingerprint, and the fingerprint
    /// is the caller's to choose.
    /// </param>
    /// <param name="PerPrinter">
    /// Permits one printer may spend, or null for a route where no printer can be read off the
    /// request - the two registration verbs, which get the ceiling alone.
    /// </param>
    private readonly record struct PolicyLimits(int Ceiling, int? PerPrinter);
}
