using System;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.Middleware;

namespace Homespool.Host.Pages.Account;

/// <summary>
/// A per-address ceiling on the anonymous pages that check a credential: the sign-in form, both
/// second-factor pages, and the three that spend a mailed link.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cost being bounded is work, not guesses.</b> Guessing is already bounded per account - a
/// wrong password and a wrong authenticator code both count against the lockout, and a recovery code
/// is long enough that brute force is not the risk. What nothing bounded is the hashing: an
/// identifier no account holds still runs a full password verification, deliberately, so that the
/// response time does not say whether the account exists. There is no account to lock, so an
/// unlimited caller could spend the appliance's CPU with no credential at all. The second thing this
/// covers is spraying, which a per-account counter structurally cannot see: one password tried
/// against many usernames trips nothing.
/// </para>
/// <para>
/// <b>Only when an address means a client.</b> The limiter is off unless the deployment has said
/// which peer is its proxy (<see cref="XForwardedOptions.TrustsAnything"/>), and this is the
/// load-bearing decision rather than a caveat. Without that, forwarded headers are ignored and every
/// visitor arrives as the proxy's own address - one partition for the whole world, where a single
/// flood would close the sign-in form to everybody including the administrator who would fix it.
/// Refusing to limit is the safer failure: the per-account lockout is still underneath, and the
/// startup warning about untrusted proxies says this is off. The cost is that a deployment serving
/// Kestrel straight to the internet gets no ceiling either, which is the price of the rule being one
/// question rather than a guess about what is in front.
/// </para>
/// <para>
/// <b>POST handlers only, so a flood cannot stop the pages rendering</b>, and the passkey challenge
/// keeps the rule it already had: it is limited whether or not a proxy is trusted, because a
/// challenge has the password form beside it as a fallback where the form itself has nothing
/// underneath. Its numbers are that policy's, so the two cannot drift apart.
/// </para>
/// </remarks>
public static class SignInRateLimit
{
    /// <summary>The policy name, for <see cref="EnableRateLimitingAttribute"/> on the pages it covers.</summary>
    public const string PolicyName = "sign-in";

    /// <summary>How many credential attempts one address may make in a <see cref="Window"/>.</summary>
    /// <remarks>
    /// Far above what a household presses - a person mistyping a password twice and asking for a
    /// reset is three - and three orders of magnitude below the rate that makes a hashing flood worth
    /// running. A shared NAT is the case that decides the number, not the attacker.
    /// </remarks>
    public const int PermitLimit = 30;

    /// <summary>The window <see cref="PermitLimit"/> applies to.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string UnknownAddress = "unknown";

    public static IServiceCollection AddSignInRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, Partition);
        });

        return services;
    }

    /// <summary>
    /// Which window <paramref name="context"/> falls in, or no limiter at all.
    /// </summary>
    /// <remarks>
    /// Public so the decision can be tested without a request going through the pipeline: the branch
    /// that matters is the one that declines to limit, and a test driving HTTP cannot tell that apart
    /// from a window that simply had room.
    /// </remarks>
    public static RateLimitPartition<string> Partition(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The login page carries this policy rather than the passkey one, a page being allowed only
        // one; the challenge keeps its own limits and its own answer to an untrusted proxy.
        if (PasskeyChallengeRateLimit.IsChallenge(context))
        {
            return WindowFor(context, PasskeyChallengeRateLimit.PermitLimit, PasskeyChallengeRateLimit.Window);
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        XForwardedOptions forwarded = context.RequestServices.GetRequiredService<IOptions<XForwardedOptions>>().Value;

        return forwarded.TrustsAnything
                   ? WindowFor(context, PermitLimit, Window)
                   : RateLimitPartition.GetNoLimiter(string.Empty);
    }

    private static RateLimitPartition<string> WindowFor(HttpContext context, int permitLimit, TimeSpan window)
    {
        return RateLimitPartition.GetFixedWindowLimiter(AddressOf(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
        });
    }

    /// <summary>
    /// The client's address as the forwarded-headers middleware has resolved it by now.
    /// </summary>
    private static string AddressOf(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? UnknownAddress;
    }
}
