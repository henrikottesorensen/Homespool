using System;

namespace Homespool.Model;

/// <summary>
/// Who is asking, and what the credential they arrived on lets them ask for: a user id and a scope.
/// The domain services take one of these wherever they used to take a bare <c>long userId</c> and
/// were deciding something with it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It replaces <c>long userId</c> rather than sitting beside it, and that is the whole argument.</b>
/// A scope passed as an <i>extra</i> parameter fails open silently the first time somebody adds an
/// endpoint and omits it. Replacing the parameter leaves no weaker overload to fall into, so the
/// compiler forbids forgetting.
/// </para>
/// <para>
/// <b>Two named factories and no nullable scope</b>, so an unscoped caller is a deliberate construction
/// that one grep enumerates. An empty slot meaning "unrestricted" reads identically to a bug; a call to
/// <see cref="Unscoped"/> does not.
/// </para>
/// <para>
/// <b>It carries no membership.</b> Membership is per team and resolved per printer, and freezing it
/// in here would break the property that makes revocation bite - the queue loop re-checking at send
/// time. A caller says what the <i>credential</i> permits; the access services still ask what the
/// <i>team</i> permits, and the answer is the intersection.
/// </para>
/// <para>
/// <b>This is not an HTTP identity in domain clothing.</b> The argument against passing one down was
/// that it couples the domain to the framework. A user id and a <see cref="CapabilitySet"/> are domain
/// vocabulary; nothing here knows what a claim is.
/// </para>
/// </remarks>
public sealed class Caller
{
    private Caller(long userId, CapabilitySet scope)
    {
        UserId = userId;
        Scope = scope;
    }

    /// <summary>The account this request acts as.</summary>
    public long UserId { get; }

    /// <summary>
    /// What the credential permits. A credential that named no subset - a signed-in browser session,
    /// or the queue loop acting on the authority a print was queued under - carries
    /// <see cref="CapabilitySet.Everything"/>: intersecting with every capability is identity, so a
    /// full scope is a scope like any other rather than a second kind of credential needing an
    /// <c>is null</c> branch in every reader.
    /// </summary>
    public CapabilitySet Scope { get; }

    /// <summary>
    /// The scope to store when this caller's authority has to outlive the request that carried it -
    /// work accepted now and acted on later by something with no credential of its own.
    /// </summary>
    /// <remarks>
    /// An unscoped caller therefore records <see cref="CapabilitySet.Everything"/> rather than an
    /// empty string: the stored row means exactly what the request meant - bounded by the owner's
    /// memberships and nothing else - while the column keeps one shape with one reading. Recording
    /// "nothing" would silently strand the work.
    /// </remarks>
    public string ScopeToRecord => CapabilitySet.Format(Scope);

    /// <summary>
    /// A caller whose credential permits everything its owner's memberships permit - the ordinary
    /// case today, and the only case a cookie session can produce.
    /// </summary>
    public static Caller Unscoped(long userId)
    {
        return new Caller(userId, CapabilitySet.Everything);
    }

    /// <summary>
    /// A caller whose credential named a subset. <paramref name="scope"/> can only ever narrow: the
    /// access services intersect it with the membership, and an intersection cannot produce a
    /// capability neither side held.
    /// </summary>
    public static Caller Scoped(long userId, CapabilitySet scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return new Caller(userId, scope);
    }

    /// <summary>
    /// Whether the <i>credential</i> permits <paramref name="capability"/>. This is the second of two
    /// questions - the first, whether the team permits it, is the access service's - and for
    /// resources with no team, such as the caller's own files, it is the only question.
    /// </summary>
    public bool Allows(Capability capability)
    {
        return Scope.Allows(capability);
    }
}
