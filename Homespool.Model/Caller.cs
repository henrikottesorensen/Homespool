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
/// <b>Not the thing <c>notes/printer-authorisation.md</c> rejected.</b> That argument was against
/// handing domain services an HTTP identity and coupling the domain to the framework. A user id and a
/// <see cref="CapabilitySet"/> are domain vocabulary; nothing here knows what a claim is.
/// </para>
/// </remarks>
public sealed class Caller
{
    private Caller(long userId, CapabilitySet? scope)
    {
        UserId = userId;
        Scope = scope;
    }

    /// <summary>The account this request acts as.</summary>
    public long UserId { get; }

    /// <summary>
    /// What the credential permits, or <c>null</c> for a credential that named no subset - a signed-in
    /// browser session, or the queue loop acting on the authority a print was queued under.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="Allows"/> rather than directly: the null is an implementation detail of
    /// "no narrowing", and a caller that tests it by hand will get the fail-open direction wrong the
    /// day somebody edits the meaning.
    /// </remarks>
    public CapabilitySet? Scope { get; }

    /// <summary>Whether this credential narrowed anything at all.</summary>
    public bool IsScoped => Scope is not null;

    /// <summary>
    /// The scope to store when this caller's authority has to outlive the request that carried it -
    /// work accepted now and acted on later by something with no credential of its own.
    /// </summary>
    /// <remarks>
    /// <b>An unscoped caller records <see cref="CapabilitySet.Everything"/> rather than an empty
    /// string or a null.</b> Intersecting with every capability is identity, so the stored row means
    /// exactly what the request meant - bounded by the owner's memberships and nothing else - while
    /// keeping the column one shape with one reading. Recording "nothing" would silently strand the
    /// work; recording a null would put an <c>is null</c> in the loop that reads it.
    /// </remarks>
    public string ScopeToRecord =>
        CapabilitySet.Format(Scope is null ? CapabilitySet.Everything : Scope.Granted);

    /// <summary>
    /// A caller whose credential permits everything its owner's memberships permit - the ordinary
    /// case today, and the only case a cookie session can produce.
    /// </summary>
    public static Caller Unscoped(long userId)
    {
        return new Caller(userId, null);
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
        return Scope is null || Scope.Allows(capability);
    }
}
