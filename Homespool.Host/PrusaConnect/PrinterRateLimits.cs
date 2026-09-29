using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

using Homespool.Host.Middleware;
using Homespool.Host.RateLimiting;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Caps how fast the printer endpoints can be hit: a window per printer where the request names one,
/// under a ceiling on each route's total. These are the only routes an unauthenticated caller on the
/// internet can reach, and both registration verbs cost something: <c>POST /p/register</c> writes a
/// database row per call, and <c>GET /p/register</c> is a guessing oracle for a pending
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
/// <b>What this does not fix.</b> The ceiling is acquired before any partition, so a caller rotating
/// fingerprints who fills it refuses every printer on that route, enrolled or not - a printer's own
/// window is never reached. What the partition stops is one identity filling the ceiling alone. Nor
/// is a printer's window its own against anyone who knows its fingerprint, which is an identifier
/// rather than a secret: they spend that window as the printer. Telling an enrolled printer from an
/// invented one needs identity this middleware does not have - it runs before authentication,
/// deliberately, so a rejected request costs no database work.
/// </para>
/// <para>
/// <b>The two registration verbs are partitioned by address instead, and only where an address is a
/// client</b> (<see cref="XForwardedOptions.AddressesAreClients"/>). Neither names a printer this can
/// read, so the address is the only partition they can have - and without one, a single caller
/// spending the ceiling refuses every printer enrolling anywhere. Elsewhere the address may be one
/// for the whole world: with no proxy trusted every request carries the proxy's, and Docker Desktop
/// rewrites every client to its VM gateway before the proxy sees it. A window per address there
/// would be one small window for everybody, far tighter than the ceiling it sits under, so the
/// verbs keep the ceiling alone. The fingerprinted routes stay on the fingerprint, which has no such
/// failure mode: it is either present and names one printer, or absent, in which case every request
/// lacking it shares one window.
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
    /// How many code requests one address may make in a <see cref="Window"/>, where addresses are
    /// clients. Buddy spends at most three - it gives up for good after three refusals - and the SDK
    /// one.
    /// </summary>
    public const int RegistrationStartPerAddressLimit = 5;

    /// <summary>
    /// How many registration polls every caller together may make in a <see cref="Window"/>. Buddy
    /// polls every 5s, about 12 a minute; the SDK about once a second, so a few printers enrolling
    /// at once sit well under this.
    /// </summary>
    /// <remarks>
    /// Buddy's poll carries a <c>Code</c> header and nothing else, so
    /// <see cref="RateLimitPolicies.PrinterRegistrationPoll"/> has no per-printer window either.
    /// Partitioning on the code would give a guesser a fresh window per guess, which bounds nothing;
    /// this ceiling is what bounds the oracle.
    /// </remarks>
    public const int RegistrationPollCeiling = 300;

    /// <summary>
    /// How many registration polls one address may make in a <see cref="Window"/>, where addresses
    /// are clients: one SDK printer's once a second, with room.
    /// </summary>
    /// <remarks>
    /// <b>A refused poll is fatal to an SDK printer's enrolment</b> - it drops the poll on any answer
    /// but 200 or 202 - where Buddy retries forever. So the window is sized for the SDK. What it does
    /// not cover is an SDK printer and a Buddy enrolling at the same moment behind one address, a
    /// router's hairpin NAT for instance, which together poll about 72 times a minute.
    /// </remarks>
    public const int RegistrationPollPerAddressLimit = 65;

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
    /// The partition a connection with no address shares - a test host, or a unix socket.
    /// </summary>
    private const string UnknownAddress = "unknown";

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
    /// <b>A policy has at most one partition: per printer, or per address.</b> The two registration
    /// verbs name no printer this middleware can read, so theirs is the address - which exists only
    /// where addresses are clients, and otherwise they get the ceiling and nothing else. Both nulls
    /// are deliberate values here rather than absent entries, so the table lists every policy and a
    /// reader can see which window each one has and why.
    /// </para>
    /// <para>
    /// Frozen because it is read on the global limiter's per-request path - which the measurement in
    /// this class's own remarks shows running a second time for a refused request - and written once.
    /// </para>
    /// </remarks>
    private static readonly FrozenDictionary<string, PolicyLimits> Policies =
        new Dictionary<string, PolicyLimits>(StringComparer.Ordinal)
        {
            [RateLimitPolicies.PrinterRegistrationStart] = new(RegistrationStartCeiling, null, RegistrationStartPerAddressLimit),
            [RateLimitPolicies.PrinterRegistrationPoll] = new(RegistrationPollCeiling, null, RegistrationPollPerAddressLimit),
            [RateLimitPolicies.PrinterSocket] = new(SocketCeiling, SocketPerPrinterLimit, null),
            [RateLimitPolicies.PrinterHttpTransport] = new(HttpTransportCeiling, HttpTransportPerPrinterLimit, null),
            [RateLimitPolicies.PrinterFile] = new(FileCeiling, FilePerPrinterLimit, null),
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
                return policy is not null && Policies.TryGetValue(policy, out PolicyLimits limits) ?
                           Ceiling(policy, limits.Ceiling) :
                           RateLimitPartition.GetNoLimiter(string.Empty);
            });

            foreach (string policy in Policies.Keys)
            {
                options.AddPolicy(policy, context => Partition(policy, context));
            }
        });

        return services;
    }

    /// <summary>
    /// Which window a request under <paramref name="policy"/> falls in beneath the ceiling: its
    /// printer's, its address's, or none.
    /// </summary>
    /// <remarks>
    /// Public so the decision can be tested without a request going through the pipeline: the branch
    /// that matters is the one that declines to partition by address, and from the outside that is
    /// indistinguishable from a window with room left in it. The key is the observable - the
    /// fingerprint or the address for a window, empty for none.
    /// </remarks>
    public static RateLimitPartition<string> Partition(string policy, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        PolicyLimits limits = Policies[policy];

        if (limits.PerPrinter is { } perPrinter)
        {
            return PerPrinter(context, perPrinter);
        }

        // Read per request, as the sign-in limit does: the rate limiter's options are configured with
        // no service provider to ask, so there is nowhere earlier to read it.
        if (limits.PerAddress is { } perAddress &&
            context.RequestServices.GetRequiredService<IOptions<XForwardedOptions>>().Value.AddressesAreClients)
        {
            return PerAddress(context, perAddress);
        }

        return RateLimitPartition.GetNoLimiter(string.Empty);
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
        string printer = context.Request.Headers.TryGetValue(Headers.Fingerprint, out StringValues fingerprint) &&
                         !StringValues.IsNullOrEmpty(fingerprint) ?
                             PrinterFingerprint.Key(fingerprint.ToString()) :
                             Unattributed;

        return RateLimitPartition.GetFixedWindowLimiter(printer, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0,
        });
    }

    /// <summary>
    /// One window per client address, as the forwarded-headers middleware has resolved it by now.
    /// </summary>
    /// <remarks>
    /// The ceiling is acquired first, so a caller rotating addresses can create no more partitions in
    /// a window than the ceiling admits - the same bound the fingerprint partition relies on.
    /// </remarks>
    private static RateLimitPartition<string> PerAddress(HttpContext context, int permitLimit)
    {
        string address = context.Connection.RemoteIpAddress?.ToString() ?? UnknownAddress;

        return RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = 0,
        });
    }

    /// <summary>
    /// What one policy is allowed: a ceiling on the route's total, and a window per printer or per
    /// address beneath it.
    /// </summary>
    /// <param name="Ceiling">
    /// Permits every caller together may spend on this policy's route in a <see cref="Window"/>.
    /// Required, because a route with no ceiling is bounded only per fingerprint, and the fingerprint
    /// is the caller's to choose.
    /// </param>
    /// <param name="PerPrinter">
    /// Permits one printer may spend, or null for a route where no printer can be read off the
    /// request - the two registration verbs.
    /// </param>
    /// <param name="PerAddress">
    /// Permits one client address may spend, where addresses are clients, or null for a route that
    /// has a printer to partition on instead.
    /// </param>
    private readonly record struct PolicyLimits(int Ceiling, int? PerPrinter, int? PerAddress);
}
