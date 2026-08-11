using System;
using System.Globalization;

using Microsoft.Extensions.Localization;

namespace Homespool.Host.Localisation;

/// <summary>
/// Picks the right plural form for a count, from a pair of resource keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to stop a number being concatenated to a noun.</b> Around 322 string literals in
/// the application already carry a count or a unit word, and every one written as
/// <c>$"{n} files"</c> is a site somebody has to find again later. A helper that is here from the
/// start means no new one is written that way.
/// </para>
/// <para>
/// <b>Two forms is the seam, not the answer.</b> English and Danish both take exactly two — one
/// file / two files, én fil / to filer — so a One/Other pair covers everything shipped today.
/// Polish and Russian take three, Arabic six, and those need a real CLDR plural rule rather than a
/// bigger <c>if</c>. Routing every call site through here now means adding that rule is one change
/// in one file instead of an archaeology exercise; guessing at it before a language needs it would
/// be inventing a category system for nobody.
/// </para>
/// <para>
/// <b>Zero takes the "other" form</b>, which is right for both current languages ("0 files",
/// "0 filer") and is not universal either. Same argument: the seam is what makes it fixable.
/// </para>
/// </remarks>
public static class Plural
{
    /// <summary>The suffix for the singular resource key.</summary>
    public const string OneSuffix = "_One";

    /// <summary>The suffix for every other count's resource key.</summary>
    public const string OtherSuffix = "_Other";

    /// <summary>
    /// The counted phrase, e.g. <c>Format(localiser, "Files", 3)</c> reading <c>Files_Other</c> and
    /// yielding "3 files".
    /// </summary>
    /// <param name="localiser">Where the pair of keys is looked up.</param>
    /// <param name="keyPrefix">The shared part of the key pair, without a suffix.</param>
    /// <param name="count">How many, which chooses the form and is formatted into it.</param>
    /// <remarks>
    /// The count is formatted by the localiser through <c>string.Format</c>, so it picks up the
    /// current culture's digit grouping and separators — <c>1,024</c> against <c>1.024</c> — rather
    /// than being pasted in as an invariant number.
    /// </remarks>
    public static string Format(IStringLocalizer localiser, string keyPrefix, int count)
    {
        ArgumentNullException.ThrowIfNull(localiser);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        string key = string.Create(
            CultureInfo.InvariantCulture,
            $"{keyPrefix}{(count == 1 ? OneSuffix : OtherSuffix)}");

        return localiser[key, count];
    }
}
