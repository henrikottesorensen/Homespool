using System;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Model.Entities;

/// <summary>
/// A personal access token: one long-lived bearer credential a person creates for a script, acting as
/// its owner and never beyond the scope it was minted with. Presented as
/// <c>Authorization: Bearer hs_&lt;43 chars&gt;</c>, and stored here only as the hash of its secret.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="TokenHash"/> is indexed, and finding the row <em>is</em> the verification.</b> A hit
/// means the presented secret hashed to a stored value, which means the preimage matched — so there
/// is no second comparison, no salt and no PBKDF2. That is a deliberate departure from
/// <c>TokenService</c>, which protects the printer token: slow salted hashing defends
/// <em>guessable</em> secrets, and 32 CSPRNG bytes are not guessable at any work factor. A salt would
/// also make this column unindexable and reintroduce the "find the row first" problem it exists to
/// avoid. See <c>notes/api-tokens.md</c>.
/// </para>
/// <para>
/// <b>No expiry and no last-used stamp.</b> Either is one nullable column if it is ever genuinely
/// wanted. A last-used stamp is the more expensive of the two and not by a little: it is a write on
/// every authenticated request, so it wants a question actually being asked before it is paid for.
/// </para>
/// <para>
/// <b><see cref="Scope"/> narrows and never grants, and that is the line between this and a
/// badly-implemented JWT.</b> It is evaluated <em>after</em> the membership gate rather than instead
/// of it, so a token can only ever subtract from what its owner may already do — <b>if a scope can be
/// made to widen anything, the design has gone wrong</b>. That is the constraint to check a change
/// against, because a scope system drifting toward claims, audiences and lifetime policies is simply a
/// worse JWT.
/// </para>
/// <para>
/// <b>Authorisation itself stays out of this table.</b> The authentication handler turns the row into
/// a <c>Caller</c>, and the access services intersect that with the membership held on each printer or
/// camera — so rights are re-read per request and a membership change bites immediately, which a
/// credential asserting its own rights could not manage. See <c>notes/permission-vocabulary.md</c>.
/// </para>
/// <para>
/// Revocation is deleting the row, which is the whole reason this is a table rather than a signed
/// token.
/// </para>
/// </remarks>
public class ApiToken
{
    /// <summary>
    /// The maximum length of a <see cref="Name"/>. Long enough to say which machine holds the token,
    /// short enough that it cannot be used to deface the page listing it.
    /// </summary>
    public const int NameMaxLength = 64;

    /// <summary>The longest <see cref="Scope"/> string the column has to hold.</summary>
    /// <remarks>Matches <c>TeamMember.CapabilitiesMaxLength</c>: a scope cannot usefully be longer than
    /// the membership it narrows.</remarks>
    public const int ScopeMaxLength = 512;

    public long Id { get; set; }

    /// <summary>
    /// The user this token acts as. Its rights are theirs, narrowed by <see cref="Scope"/> and never
    /// exceeding them.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// SHA-384 of the token's secret, base64url-encoded. Uniquely indexed: the lookup is the
    /// verification.
    /// </summary>
    /// <remarks>
    /// The algorithm is pinned — see <c>ApiTokenService</c> for why a lookup key in particular
    /// cannot afford to vary by host.
    /// </remarks>
    public required string TokenHash { get; set; }

    /// <summary>
    /// What the owner calls this token, so they know which one is safe to revoke. Not unique: two
    /// tokens named "laptop" are the owner's problem, not a constraint worth enforcing.
    /// </summary>
    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// What this token may do, as <c>CapabilitySet</c> spells it. <b>Every token has one.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not nullable, because there is nothing for a null to mean.</b> A token that narrows nothing
    /// is one scoped to <c>CapabilitySet.Everything</c> - intersecting with every capability is
    /// identity, so the two are the same credential by construction. A nullable column would have
    /// bought a second spelling of that and an <c>is null</c> for every reader to get right.
    /// </para>
    /// <para>
    /// <b>Empty grants nothing</b>, deliberately and sayably: a token can be minted powerless.
    /// </para>
    /// <para>
    /// <b>It cannot widen its owner.</b> A scope naming <c>ManagePrinter</c> on a printer its owner
    /// only reads still manages nothing - the gates intersect, and the membership half is unchanged.
    /// </para>
    /// </remarks>
    [MaxLength(ScopeMaxLength)]
    public required string Scope { get; set; }
}
