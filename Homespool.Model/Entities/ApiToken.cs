using System;
using System.ComponentModel.DataAnnotations;

namespace Homespool.Model.Entities;

/// <summary>
/// A personal access token: one long-lived bearer credential a person creates for a script, acting
/// with exactly their own rights. Presented as <c>Authorization: Bearer hs_&lt;43 chars&gt;</c>, and
/// stored here only as the hash of its secret.
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
/// <b>No scopes, no expiry, no last-used stamp.</b> A token inherits its owner's rights entirely and
/// authorisation stays where it already is, in <c>TeamMember.CanUse</c> via
/// <c>PrinterCommandService</c>. Adding rights and lifetimes here is how a personal access token turns
/// into a badly-implemented JWT; expiry is one nullable column if it is ever genuinely wanted.
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

    /// <summary>The user this token acts as. Its rights are theirs, in full.</summary>
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
