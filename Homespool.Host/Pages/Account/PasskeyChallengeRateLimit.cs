using System;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

using Homespool.Host.Pages.Account.Manage;
using Homespool.Host.RateLimiting;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// A per-address ceiling on how often a passkey ceremony may be asked for: the login page's
/// challenge, which is anonymous, and the Manage page's, which is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>This policy is carried by the Manage page alone.</b> A page may hold one
/// <see cref="EnableRateLimitingAttribute"/>, and the login page holds
/// <see cref="SignInRateLimit"/>, which limits its credential handlers as well - reading
/// <see cref="IsChallenge"/> and these numbers, so the challenge there keeps exactly this ceiling.
/// The one behavioural difference is that the two pages' challenges no longer share a window.
/// </para>
/// <para>
/// <b>Defence in depth, not the defence.</b> Since the ledger records answers rather than
/// challenges, a challenge costs the server nothing to remember and a flood of them cannot fill it;
/// what a flood still costs is the engine's work of minting options, and this bounds that per
/// address. It is what the 2026-09-06 review asked for alongside the ledger fix.
/// </para>
/// <para>
/// <b>Why the policy picks the handler and not the attribute.</b> Rate-limiting metadata attaches to
/// a Razor page as a whole, and both challenge handlers share their page with a password form or a
/// list of passkeys that must not be throttled. So the attribute goes on the page, and the policy
/// hands every request that is not a challenge a partition with no limiter.
/// </para>
/// <para>
/// <b>Per address, as the proxy reports it.</b> The address is whatever the forwarded-headers
/// middleware has resolved by the time the limiter runs, which is the client's when a proxy is
/// trusted and the proxy's own otherwise - in which case every visitor shares one window, and the
/// operator's warning at startup already says so. A household behind one NAT shares a window too;
/// thirty a minute is many more than a household presses the button.
/// </para>
/// </remarks>
public static class PasskeyChallengeRateLimit
{
    /// <summary>How many challenges one address may ask for in a <see cref="Window"/>.</summary>
    public const int PermitLimit = 30;

    /// <summary>The window <see cref="PermitLimit"/> applies to.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string HandlerQuery = "handler";

    private const string UnknownAddress = "unknown";

    public static IServiceCollection AddPasskeyChallengeRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(RateLimitPolicies.PasskeyChallenge, context => IsChallenge(context)
                ? RateLimitPartition.GetFixedWindowLimiter(AddressOf(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitLimit,
                    Window = Window,
                    QueueLimit = 0,
                })
                : RateLimitPartition.GetNoLimiter(string.Empty));
        });

        return services;
    }

    /// <summary>Whether <paramref name="context"/> asks one of the two pages for a ceremony.</summary>
    public static bool IsChallenge(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.Query.TryGetValue(HandlerQuery, out StringValues handler))
        {
            return false;
        }

        return string.Equals(handler, LoginModel.PasskeyOptionsHandler, StringComparison.OrdinalIgnoreCase)
               || string.Equals(handler, PasskeysModel.BeginRegistrationHandler, StringComparison.OrdinalIgnoreCase);
    }

    private static string AddressOf(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? UnknownAddress;
    }
}
