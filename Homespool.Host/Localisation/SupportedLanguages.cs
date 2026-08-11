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
    /// <para>
    /// <c>da</c> stays neutral because Danish is one language in one region and naming <c>da-DK</c>
    /// would claim something nobody stated. English does not have that luxury, so it names the
    /// region it is actually written in. Requests match in both directions — <c>en</c> selects
    /// <c>en-GB</c>, <c>da-DK</c> selects <c>da</c> — see <see cref="Matches"/>.
    /// </para>
    /// <para>
    /// <b><c>en-US</c> ships with no resource file of its own, and that is not an oversight.</b> Not
    /// one string in the application currently differs between the two dialects, so a resource file
    /// would be an exact copy of the neutral one — and a copy is worse than nothing, because the
    /// next edit updates one of them. What <c>en-US</c> buys today is entirely the <i>formatting</i>
    /// half: <c>3/9/2026</c> and a 12-hour clock against <c>09/03/2026</c> and a 24-hour one. It
    /// falls back to the neutral resources for words, which is the correct behaviour right up until
    /// somebody writes "customise" or "colour" into one — at which point this needs a file
    /// containing exactly the words that differ, and nothing else.
    /// </para>
    /// <para>
    /// <b>Order decides what plain <c>en</c> selects</b>, and it selects the first match, so
    /// <c>en-GB</c> being first is what makes an unqualified <c>Accept-Language: en</c> land on the
    /// default rather than on American formatting.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> CultureNames = [DefaultCulture, "en-US", "da"];

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
    /// <remarks>
    /// <b>"UK" here, <c>en-GB</c> in the culture name, and both are right.</b> The label is what a
    /// person reads, and in English the country is the United Kingdom; the culture name is a BCP 47
    /// identifier, where the region subtag comes from ISO 3166-1 and is <c>GB</c>. The two
    /// disagreeing is a property of the standards, not a mistake to tidy — see
    /// <see cref="DefaultCulture"/> for what happens to somebody who tidies it.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultCulture] = "English (UK)",
            ["en-US"] = "English (US)",
            ["da"] = "Dansk",
        };

    /// <summary>
    /// The names on 1 April.
    /// </summary>
    /// <remarks>
    /// Danish is untouched: the joke is about the two Englishes, and a language that is not part of
    /// it should not be dragged in.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> AprilNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultCulture] = "English (Traditional)",
            ["en-US"] = "English (Simplified)",
            ["da"] = "Dansk",
        };

    /// <summary>
    /// What to call each language on a given day (Henrik, 2026-08-11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Labels only, and that is what makes it safe.</b> The values a form posts are culture
    /// names, so nothing about selecting, storing or resolving a language changes on 1 April — the
    /// worst case is somebody screenshotting a picker that reads oddly. A joke wired any deeper than
    /// the label would be a bug with a punchline.
    /// </para>
    /// <para>
    /// <b>Takes the date rather than reading a clock</b>, so it is testable on the other 364 days.
    /// Callers pass local time: the question is whether it is April Fools' where this Homespool is
    /// installed, which is the same reasoning that put <c>TZ</c> in the container.
    /// </para>
    /// </remarks>
    /// <param name="today">The current local date.</param>
    public static IReadOnlyDictionary<string, string> DisplayNamesOn(DateTimeOffset today)
    {
        return today is { Month: 4, Day: 1 } ? AprilNames : DisplayNames;
    }

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
