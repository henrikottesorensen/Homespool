using Microsoft.AspNetCore.Identity;

using Squint;

namespace Homespool.Host.Accounts;

/// <summary>
/// The lookup key for a username is its UTS #39 skeleton, upper-cased: two names that look alike
/// are the same name. Email addresses keep Identity's plain upper-invariant key.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes lookalikes harmless without excluding any letter.</b> Identity keeps
/// <c>NormalizedUserName</c> beside <c>UserName</c> and puts the unique index on the normalised one,
/// so whatever this method returns is what uniqueness and <c>FindByNameAsync</c> are measured on.
/// With the skeleton as the key, a Cyrillic <c>а</c> in <em>henrik</em>, <em>þor</em> beside
/// <em>por</em>, <em>Ægir</em> beside <em>AEgir</em> and <em>rnodern</em> beside <em>modern</em>
/// all resolve to one account, and the second registration is refused as a duplicate. Nothing
/// has to be forbidden for that, which is why the username character set could stop being a list.
/// </para>
/// <para>
/// <b>NFKC first, then the skeleton, then upper-case</b> - the order the specification composes them
/// in, and upper-casing last because the skeleton maps some capitals to lower-case prototypes
/// (<c>I</c> becomes <c>l</c>) and the key must not depend on which case was typed. The skeleton is
/// computed from Squint's own tables rather than the machine's ICU, so the key is the same on every
/// platform this runs on, which a stored unique key has to be.
/// </para>
/// <para>
/// <b>A stored key is only as current as the Unicode data that made it.</b> A Squint upgrade with new
/// confusable data can change what a name's skeleton is, so every user's key is recomputed at start-up
/// - see <see cref="UsernameKeyRefresh"/> - rather than trusted from the row.
/// </para>
/// <para>
/// Role names go through <see cref="NormalizeName"/> as well, because Identity has one normaliser for
/// both. The one role, <c>Admin</c>, is its own skeleton, so its stored key is unchanged.
/// </para>
/// </remarks>
public sealed class SkeletonLookupNormalizer : ILookupNormalizer
{
    /// <summary>The upper-cased skeleton of the NFKC form of <paramref name="name"/>.</summary>
    public string? NormalizeName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        return Confusables.Skeleton(Normalization.Nfkc(name)).ToUpperInvariant();
    }

    /// <summary>Identity's own key for an address: upper-invariant, nothing more.</summary>
    public string? NormalizeEmail(string? email)
    {
        return email?.ToUpperInvariant();
    }
}
