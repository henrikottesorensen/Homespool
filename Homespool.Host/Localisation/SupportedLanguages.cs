using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Homespool.Host.Localisation;

/// <summary>
/// The languages Homespool ships, and the only place that says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>A list rather than an enum</b>, and not in the schema. Adding a language should be adding a
/// resource file and a line here, never a migration — which is why <c>HSUser.Language</c> stores a
/// culture name and this class is what validates it.
/// </para>
/// <para>
/// <b>Danish is here as the proof, not as an afterthought.</b> A single-culture "localisation ready"
/// cannot be tested: every lookup returns the neutral string whether the wiring works or not, so the
/// first person to discover the pipeline never switched is whoever adds the second language later.
/// <c>da</c> also formats differently enough to make a culture bug visible — <c>dd-MM-yyyy</c> and a
/// comma decimal separator — where a second English variant would hide one.
/// </para>
/// </remarks>
public static class SupportedLanguages
{
    /// <summary>
    /// The language used when nothing else is known, and the one the neutral resources are written
    /// in.
    /// </summary>
    /// <remarks>
    /// <b><c>en-GB</c> rather than <c>en</c></b> (Henrik, 2026-08-11: <i>"English traditional
    /// (GB)"</i>). The codebase already spells it that way throughout — <c>Authorisation</c>,
    /// <c>memoises</c>, <c>localisation</c> — so naming the region makes the prose in the resources
    /// and the prose in the source the same dialect rather than accidentally two. It also picks up
    /// <c>dd/MM/yyyy</c> and a 24-hour clock, which is what the rest of the deployment already
    /// assumes.
    /// </remarks>
    /// <seealso href="https://www.rfc-editor.org/rfc/rfc5646">
    /// <b>It is <c>en-GB</c>, never <c>en-UK</c>, and the wrong one does not fail loudly.</b>
    /// Measured 2026-08-11: <c>CultureInfo.GetCultureInfo("en-UK")</c> throws nothing — ICU invents a
    /// <i>user custom culture</i>, which reports its English name as "English (United Kingdom)" and
    /// then formats 9 March 2026 as <c>3/9/2026</c>, the American order, where <c>en-GB</c> gives
    /// <c>09/03/2026</c>. So the mistake reads as correct everywhere a person would look for it.
    /// BCP 47 takes its region subtags from ISO 3166-1 alpha-2, which assigns <c>GB</c>; <c>UK</c> is
    /// only exceptionally reserved and is never the assigned code. The confusion is worth knowing
    /// rather than just the rule: the internet TLD really is <c>.uk</c>, one of the few ccTLDs that
    /// diverges from ISO, so "UK" is right in a domain name and wrong here.
    /// </seealso>
    public const string DefaultCulture = "en-GB";

    /// <summary>
    /// Every culture that may be selected, most-preferred first.
    /// </summary>
    /// <remarks>
    /// <c>da</c> stays neutral because Danish is one language in one region and naming <c>da-DK</c>
    /// would claim something nobody stated. English does not have that luxury, so it names the
    /// region it is actually written in. Requests match in both directions — <c>en</c> selects
    /// <c>en-GB</c>, <c>da-DK</c> selects <c>da</c> — see <see cref="Matches"/>.
    /// </remarks>
    public static readonly IReadOnlyList<string> CultureNames = [DefaultCulture, "da"];

    /// <summary>
    /// What each language calls itself, for a picker.
    /// </summary>
    /// <remarks>
    /// <b>Endonyms, and deliberately not translated.</b> A Dane looking for their own language scans
    /// for "Dansk", not for "Danish" rendered in whatever language the page happens to be in — and a
    /// picker whose entries move about as the page language changes is the one control that must not.
    /// Written out rather than taken from <c>CultureInfo.NativeName</c>, which yields "English
    /// (United Kingdom)" and "dansk (Danmark)": accurate, and not what anybody would print on a menu.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultCulture] = "English",
            ["da"] = "Dansk",
        };

    /// <summary>
    /// The supported cultures as <see cref="CultureInfo"/>, for the request-localisation options.
    /// </summary>
    public static IReadOnlyList<CultureInfo> Cultures { get; } =
        CultureNames.Select(CultureInfo.GetCultureInfo).ToList();

    /// <summary>
    /// Whether a stored or requested culture name is one Homespool actually ships.
    /// </summary>
    /// <remarks>
    /// Matched on the neutral part, so <c>da-DK</c> is accepted for <c>da</c>. Anything unknown is
    /// rejected rather than approximated: a stored value that no longer matches a shipped language
    /// should fall back to the browser, which is what null already means.
    /// </remarks>
    public static bool IsSupported(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        return CultureNames.Any(supported => Matches(cultureName, supported));
    }

    /// <summary>
    /// The shipped culture a name selects, or null when it selects none.
    /// </summary>
    public static string? Resolve(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        return CultureNames.FirstOrDefault(supported => Matches(cultureName, supported));
    }

    /// <summary>
    /// Whether a requested name selects a shipped language, exactly or by sharing its language part.
    /// </summary>
    /// <remarks>
    /// <b>Both directions, because the shipped set goes both ways.</b> <c>da-DK</c> has to select the
    /// neutral <c>da</c> we ship, and a browser asking for plain <c>en</c> has to select the specific
    /// <c>en-GB</c> we ship — the second is the case a one-directional check silently gets wrong,
    /// sending every <c>Accept-Language: en</c> to the default by accident rather than by match.
    /// <c>en-US</c> matches nothing here and falls back to the default, which is correct: British
    /// English is what exists, and pretending otherwise would claim a translation we do not have.
    /// </remarks>
    private static bool Matches(string requested, string supported)
    {
        if (string.Equals(requested, supported, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Compared on the prefix plus a separator rather than StartsWith alone, so "dan" does not
        // select "da" and "en-GB-oxendict" does not accidentally become its own thing.
        return SharesLanguagePart(requested, supported) || SharesLanguagePart(supported, requested);
    }

    /// <summary>
    /// Whether <paramref name="longer"/> is <paramref name="shorter"/> plus a subtag.
    /// </summary>
    private static bool SharesLanguagePart(string longer, string shorter)
    {
        return longer.Length > shorter.Length
            && longer[shorter.Length] == '-'
            && longer.AsSpan(0, shorter.Length).Equals(shorter, StringComparison.OrdinalIgnoreCase);
    }
}
