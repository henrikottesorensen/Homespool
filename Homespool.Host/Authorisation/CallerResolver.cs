using System;
using System.Security.Claims;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Authorisation;

/// <summary>
/// The one place that turns an authenticated request into a <see cref="Caller"/>. Every controller
/// and page goes through here rather than calling <see cref="Caller.Unscoped"/> itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one place, spelled out.</b> A caller is what the access services intersect against, so a
/// controller that built <see cref="Caller.Unscoped"/> for a request that arrived on a scoped token
/// would defeat scoping for that endpoint - silently, and in a way no test of the services could see.
/// Funnelling every construction through here is what keeps every edge correct at once. It is also
/// the only sensible thing to grep for.
/// </para>
/// <para>
/// <b>The scope is read from the principal, not assumed.</b> The API token handler writes
/// <see cref="HSClaimTypes.Scope"/> on every token it authenticates, and nothing writes it for a
/// sign-in cookie - so a token resolves to the capabilities its scope names and a browser session
/// resolves unscoped, from the same read. Hardcoding either answer here would fail open the day the
/// other side changed.
/// </para>
/// </remarks>
public static class CallerResolver
{
    /// <summary>
    /// The caller for a signed-in principal already resolved to its account.
    /// </summary>
    /// <param name="user">The account, as the page or controller has already loaded it.</param>
    /// <param name="principal">The request's principal, which is where the scope is read from.</param>
    public static Caller For(HSUser user, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(user);

        return For(user.Id, principal);
    }

    /// <summary>
    /// The caller for a user id and the principal the request arrived with.
    /// </summary>
    /// <remarks>
    /// <b>An absent scope claim is not an empty one.</b> No claim means the credential named no subset
    /// and narrows nothing; a claim holding an empty value is a scope that grants nothing. Reading
    /// them the same way would make a deliberately powerless token indistinguishable from a browser
    /// session.
    /// </remarks>
    public static Caller For(long userId, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? scope = principal.FindFirstValue(HSClaimTypes.Scope);

        return scope is null ? Caller.Unscoped(userId) : Caller.Scoped(userId, CapabilitySet.Parse(scope));
    }
}
