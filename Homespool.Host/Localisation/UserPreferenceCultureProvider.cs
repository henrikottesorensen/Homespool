using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Homespool.Host.Localisation;

/// <summary>
/// Reads the signed-in account's stored language, so a choice follows the person rather than the
/// browser they happen to be using.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placed ahead of the <c>Accept-Language</c> provider, and behind nothing else.</b> A stored
/// preference is the only signal that is an actual decision — the header is a guess made by whoever
/// installed the browser. Ordering it first is what makes "I chose Danish" survive signing in from
/// a machine set to English, which is the case a picker exists for.
/// </para>
/// <para>
/// <b>Answers null for an anonymous request rather than a default</b>, which is what lets the
/// remaining providers run. Returning a culture here would make every signed-out page the
/// deployment default and quietly disable content negotiation for the sign-in page itself.
/// </para>
/// <para>
/// <b>A database read per request is the cost, and it is paid only when signed in.</b> Scoped to the
/// request through the existing <c>DbContext</c>, so it joins work the request was doing anyway
/// rather than opening a connection of its own.
/// </para>
/// </remarks>
public sealed class UserPreferenceCultureProvider : RequestCultureProvider
{
    /// <inheritdoc />
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? identifier = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(identifier, out long userId))
        {
            return null;
        }

        UserCultures cultures = httpContext.RequestServices.GetRequiredService<UserCultures>();

        string? culture = await cultures
            .ForUserAsync(userId, httpContext.RequestAborted)
            .ConfigureAwait(false);

        return culture is null ? null : new ProviderCultureResult(culture);
    }
}
