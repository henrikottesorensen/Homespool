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
/// <b>A window per caller and a ceiling per route, and neither works without the other.</b> A window
/// per printer stops one caller spending everybody's allowance - which a single global window cannot,
/// so one noisy or hostile client could keep a whole fleet out. But the printer names itself with the
/// <c>Fingerprint</c> header, which is unauthenticated at this point in the pipeline and can be minted
/// fresh per request, so windows alone bound each source and bound the total at nothing. The ceiling
/// is what keeps the aggregate finite; the window is what keeps one caller from consuming it.
/// </para>
/// <para>
/// <b>Both are counted by <see cref="PrinterRouteLimiter"/>, not by two framework limiters</b>, because
/// the framework's pair got the second half wrong: a request the window refused had already spent a
/// ceiling permit, and spent another on the middleware's retry, so one caller over its own window
/// filled the ceiling for everybody. That class's remarks carry the reproduction. It sits in the
/// global slot, which is the only one that takes a whole limiter of our own; the endpoint policies
/// below are names with no limiter behind them, there so that <c>[EnableRateLimiting]</c> resolves.
/// </para>
/// <para>
/// <b>What this does not fix.</b> A caller rotating fingerprints meets a fresh window each time and so
/// spends the ceiling, refusing every printer on that route, enrolled or not. Nor is a printer's
/// window its own against anyone who knows its fingerprint, which is an identifier rather than a
/// secret: they spend that window as the printer. Telling an enrolled printer from an invented one
/// needs identity this does not have - it runs before authentication, deliberately, so a rejected
/// request costs no database work.
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
/// <b>This class owns the one global limiter.</b> <c>PasskeyChallengeRateLimit</c> and
/// <c>SignInRateLimit</c> configure the same options object and set only policies, so nothing here
/// reaches a page: an endpoint carrying any other policy, or none, is admitted without counting.
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
    /// Every policy this class wires, and the limits each one gets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One table, because a policy is named in two places.</b> The framework has to know the name,
    /// or an action carrying it answers 500 on every request - at request time rather than at startup,
    /// so a route no test drove would ship broken - and <see cref="PrinterRouteLimiter"/> has to know
    /// its limits, or the route is counted by nothing and answers normally throughout. Both are driven
    /// from this table, so neither can be written without the other.
    /// </para>
    /// <para>
    /// <b>A policy has at most one partition: per printer, or per address.</b> The two registration
    /// verbs name no printer this middleware can read, so theirs is the address - which exists only
    /// where addresses are clients, and otherwise they get the ceiling and nothing else. Both nulls
    /// are deliberate values here rather than absent entries, so the table lists every policy and a
    /// reader can see which window each one has and why.
    /// </para>
    /// <para>
    /// Frozen because it is read on every printer request, twice for a refused one - the middleware
    /// asks the global limiter again before giving up - and written once.
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
    /// Adds the rate limiter with <see cref="PrinterRouteLimiter"/> in its global slot, and a name for
    /// every printer policy in <see cref="Policies"/>.
    /// </summary>
    /// <remarks>
    /// The names carry no limiter of their own - <see cref="PrinterRouteLimiter"/> counts both the
    /// window and the ceiling - so the framework's endpoint limiter admits every printer request and
    /// cannot spend anything a refusal would have to give back.
    /// </remarks>
    public static IServiceCollection AddPrinterRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => new PrinterRouteLimiter(provider.GetRequiredService<TimeProvider>()));

        // Configured with the limiter from the container rather than built in the lambda below,
        // which has no service provider to ask for the clock.
        services.AddOptions<RateLimiterOptions>()
                .Configure<PrinterRouteLimiter>((options, limiter) => options.GlobalLimiter = limiter);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            foreach (string policy in Policies.Keys)
            {
                options.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter(string.Empty));
            }
        });

        return services;
    }

    /// <summary>
    /// What <paramref name="context"/> asks of the printer limits: its route's ceiling, and the
    /// window of the caller it counts as, if any. Null for an endpoint that carries no printer policy,
    /// which is every page in the application.
    /// </summary>
    /// <remarks>
    /// Derived from the endpoint's metadata rather than from the path, so the policy name stays the
    /// single thing that decides which limits an action gets.
    /// </remarks>
    public static Demand? DemandOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        return policy is not null && Policies.ContainsKey(policy) ? DemandOf(policy, context) : null;
    }

    /// <summary>
    /// What a request under <paramref name="policy"/> asks of the printer limits.
    /// </summary>
    /// <remarks>
    /// Public so the choice of caller can be tested without a request going through the pipeline: the
    /// branch that matters is the one that declines a window per address, and from the outside that
    /// is indistinguishable from a window with room left in it.
    /// </remarks>
    public static Demand DemandOf(string policy, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        PolicyLimits limits = Policies[policy];

        if (limits.PerPrinter is { } perPrinter)
        {
            return new Demand(policy, limits.Ceiling, PrinterOf(context), perPrinter);
        }

        // Read per request, as the sign-in limit does: the options are bound after this class has
        // registered anything, and a test host sets them late.
        if (limits.PerAddress is { } perAddress &&
            context.RequestServices.GetRequiredService<IOptions<XForwardedOptions>>().Value.AddressesAreClients)
        {
            return new Demand(policy, limits.Ceiling, ClientAddressKey.Of(context.Connection.RemoteIpAddress), perAddress);
        }

        return new Demand(policy, limits.Ceiling, null, null);
    }

    /// <summary>
    /// The printer a request names, keyed on the fingerprint it carries.
    /// </summary>
    /// <remarks>
    /// Reduced through <see cref="PrinterFingerprint.Key"/>, which is both what identifies an
    /// enrolled credential and a bound on the key: a caller cannot choose a key longer than
    /// <see cref="PrinterFingerprint.KeyLength"/> characters.
    /// </remarks>
    private static string PrinterOf(HttpContext context)
    {
        return context.Request.Headers.TryGetValue(Headers.Fingerprint, out StringValues fingerprint) &&
               !StringValues.IsNullOrEmpty(fingerprint) ?
                   PrinterFingerprint.Key(fingerprint.ToString()) :
                   Unattributed;
    }

    /// <summary>
    /// What one request asks of the printer limits.
    /// </summary>
    /// <param name="Policy">The route's policy, which names its ceiling.</param>
    /// <param name="Ceiling">Permits every caller together may spend on the route in a <see cref="Window"/>.</param>
    /// <param name="Caller">
    /// The printer's key or the client's address, or null where the route has no window per caller -
    /// a registration verb on a deployment whose addresses may be shared.
    /// </param>
    /// <param name="PerCaller">Permits that caller may spend in a <see cref="Window"/>, or null with no caller.</param>
    public readonly record struct Demand(string Policy, int Ceiling, string? Caller, int? PerCaller);

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
