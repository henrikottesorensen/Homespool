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
/// Funnelling every construction through here means the day tokens carry a scope, one method learns
/// to read it and every edge is correct at once. It is also the only sensible thing to grep for.
/// </para>
/// <para>
/// <b>The scope is read from the principal, not assumed.</b> Today no authentication handler writes
/// <see cref="HSClaimTypes.Scope"/>, so every credential resolves as unscoped - but that is because
/// the claim is absent, not because this method hardcodes it. When <c>ApiToken</c> gains a scope
/// column and the handler starts writing the claim, scoping begins working with no change here, which
/// is the point: the alternative fails open the day somebody adds the column and forgets this file.
/// </para>
/// </remarks>
public static class CallerResolver
{
    /// <summary>
    /// The caller for a signed-in principal already resolved to its account.
    /// </summary>
    /// <param name="user">The account, as the page or controller has already loaded it.</param>
    /// <param name="principal">
    /// The request's principal, which is where a scope will be read from once one exists. Passed now
    /// so the call sites do not have to change again when it does.
    /// </param>
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

    /// <summary>
    /// The caller for a user id with no principal to read - <b>unscoped by construction</b>. For the
    /// queue loop acting on the authority a print was queued under, and for the few places that
    /// resolve an id without a request in hand.
    /// </summary>
    /// <remarks>
    /// <b>Prefer the overloads taking a principal wherever there is one</b>, because this one cannot
    /// see a scope and so cannot honour it. The queue loop is the deliberate exception - it acts as
    /// <c>QueuedPrint.QueuedByUserId</c> with no request anywhere, and when a queue entry stores the
    /// scope it was accepted under, it will build a scoped caller from the row instead.
    /// </remarks>
    public static Caller ForUserId(long userId)
    {
        return Caller.Unscoped(userId);
    }
}
