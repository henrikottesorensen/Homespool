using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// The proxy's holding pages: one per language, all saying the same thing in the same markup.
/// </summary>
/// <remarks>
/// <para>
/// <b>This translation is the one the application cannot hold.</b> Every other string lives in
/// <c>SharedResource.resx</c> and is guarded by the parity and duplicate tests; these are served by
/// nginx at the exact moment the application is not answering, so they are a second copy of the
/// translation in a second place, with nothing to compare them against but each other. That is what
/// this file is: the parity test for the two files under <c>nginx/</c>.
/// </para>
/// <para>
/// <b>What it cannot see is the proxy.</b> Which file a request gets is decided by
/// <c>$hs_starting_language</c> in <c>homespool.conf.template</c>, and nothing here runs nginx. So
/// these assertions cover the pages being interchangeable and translated; they say nothing about one
/// being reachable.
/// </para>
/// </remarks>
public class StartingPageTests
{
    /// <summary>The language a file with no language in its name is written in.</summary>
    /// <remarks>
    /// <c>en</c> rather than <c>en-GB</c>: the page carries no date, number or dialect word, so
    /// naming a region would claim a distinction it does not make. The application's own default is
    /// still <c>en-GB</c> - see <c>SupportedLanguages</c>.
    /// </remarks>
    private const string NeutralLanguage = "en";

    /// <summary>
    /// Anything that would make the page fetch a second thing. The page's own comment states this
    /// constraint and until now nothing enforced it.
    /// </summary>
    /// <remarks>
    /// Tags rather than a search for <c>://</c>, which the mark's own <c>xmlns</c> carries and which
    /// identifies a namespace rather than requesting anything.
    /// </remarks>
    private static readonly IReadOnlyList<string> FetchingTags =
        ["link", "script", "img", "iframe", "object", "embed", "video", "audio", "source", "use"];

    private static readonly Regex HtmlComment =
        new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly Regex StyleElement =
        new(@"<style\b.*?</style>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly Regex OpeningTag =
        new(@"<(?<name>[a-zA-Z][a-zA-Z0-9-]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly Regex AnyTag =
        new(@"<[^>]*>", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly Regex DeclaredLanguage =
        new(@"<html\s+lang=""(?<language>[a-zA-Z-]+)""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    private static readonly Regex Title =
        new(@"<title>(?<title>[^<]*)</title>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The scan has to be known-good, or every assertion below passes over an empty list the day the
    /// files are renamed.
    /// </summary>
    [Fact]
    public void TheScanFindsEveryHoldingPage()
    {
        IReadOnlyList<string> pages = HoldingPages();

        pages.Should().HaveCountGreaterThanOrEqualTo(2,
                                                     "there is an English page and a Danish one, and finding fewer means the walk is not reaching nginx/");
        pages.Should().Contain(page => Path.GetFileName(page) == "starting.html");
        pages.Should().Contain(page => Path.GetFileName(page) == "starting.da.html");
    }

    /// <summary>
    /// Each page says which language it is in, and says the one its name promises.
    /// </summary>
    /// <remarks>
    /// The proxy sends <c>Content-Language</c> from the file it chose, so a page whose own
    /// <c>lang</c> disagreed would be announced in one language and marked up in another - which a
    /// screen reader believes and a person cannot see.
    /// </remarks>
    [Fact]
    public void EveryHoldingPageDeclaresTheLanguageItsNamePromises()
    {
        foreach (string page in HoldingPages())
        {
            Match declared = DeclaredLanguage.Match(File.ReadAllText(page));

            declared.Success.Should().BeTrue($"{Path.GetFileName(page)} must carry a lang attribute on <html>");
            declared.Groups["language"].Value.Should().Be(LanguageOf(page),
                                                          $"{Path.GetFileName(page)} is named for that language");
        }
    }

    /// <summary>
    /// No page fetches anything, which is the whole design of this page.
    /// </summary>
    [Fact]
    public void NoHoldingPageFetchesAnything()
    {
        foreach (string page in HoldingPages())
        {
            string markup = HtmlComment.Replace(File.ReadAllText(page), string.Empty);
            IReadOnlyList<string> tags = TagSequence(markup);

            tags.Should().NotIntersectWith(FetchingTags,
                                           $"{Path.GetFileName(page)} is served when nothing else on the deployment works, so anything it fetched would be a second thing to fail");
            markup.Should().NotContain("src=");
            markup.Should().NotContain("url(");
        }
    }

    /// <summary>
    /// Every translated page is the English page with the words changed, and nothing else.
    /// </summary>
    /// <remarks>
    /// The drift guard, and the reason the two files can be kept in two places at all: a paragraph
    /// added to one and not the other changes the tag sequence, and a style rule added to one and not
    /// the other changes the stylesheet. Neither is visible to anybody reading a single file.
    /// </remarks>
    [Fact]
    public void EveryTranslatedPageCarriesTheSameMarkupAsTheEnglishOne()
    {
        string english = File.ReadAllText(EnglishPage());

        foreach (string page in TranslatedPages())
        {
            string translated = File.ReadAllText(page);
            string name = Path.GetFileName(page);

            TagSequence(translated).Should().Equal(TagSequence(english),
                                                   $"{name} must be the same page with the words changed");
            Stylesheet(translated).Should().Be(Stylesheet(english),
                                               $"{name} must not have grown a style rule of its own");
        }
    }

    /// <summary>
    /// Every translated page has actually been translated.
    /// </summary>
    /// <remarks>
    /// <b>The falsifiable half</b>, and the one worth having: copying the English file to a language
    /// name and calling it done passes every other assertion here. The mark's <c>aria-label</c> is
    /// the product name and is deliberately not compared - it is the one string that must not
    /// change.
    /// </remarks>
    [Fact]
    public void EveryTranslatedPageIsActuallyTranslated()
    {
        string english = File.ReadAllText(EnglishPage());

        foreach (string page in TranslatedPages())
        {
            string translated = File.ReadAllText(page);
            string name = Path.GetFileName(page);

            TitleOf(translated).Should().NotBeEmpty($"{name} needs a title");
            TitleOf(translated).Should().NotBe(TitleOf(english), $"{name}'s title is still the English one");
            VisibleText(translated).Should().NotBe(VisibleText(english), $"{name} is a copy of the English page");
        }
    }

    private static DirectoryInfo RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Homespool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory
               ?? throw new InvalidOperationException($"No Homespool.slnx above {AppContext.BaseDirectory}.");
    }

    private static IReadOnlyList<string> HoldingPages()
    {
        return Directory.GetFiles(Path.Combine(RepositoryRoot().FullName, "nginx"), "starting*.html")
                        .OrderBy(page => page, StringComparer.Ordinal)
                        .ToList();
    }

    private static string EnglishPage()
    {
        return HoldingPages().Single(page => LanguageOf(page) == NeutralLanguage);
    }

    private static IEnumerable<string> TranslatedPages()
    {
        return HoldingPages().Where(page => LanguageOf(page) != NeutralLanguage);
    }

    /// <summary>
    /// The language a file is named for: <c>starting.da.html</c> is Danish, <c>starting.html</c> is
    /// the neutral English one.
    /// </summary>
    /// <remarks>
    /// Derived rather than listed, so a third language is covered by this file the moment it exists -
    /// which is the opposite of how the resource files work, and only possible because the language
    /// is in the name.
    /// </remarks>
    private static string LanguageOf(string page)
    {
        string[] parts = Path.GetFileName(page).Split('.');

        return parts.Length == 3 ? parts[1] : NeutralLanguage;
    }

    private static IReadOnlyList<string> TagSequence(string markup)
    {
        return OpeningTag.Matches(HtmlComment.Replace(markup, string.Empty))
                         .Select(match => match.Groups["name"].Value.ToLowerInvariant())
                         .ToList();
    }

    private static string Stylesheet(string markup)
    {
        Match style = StyleElement.Match(markup);

        return style.Success ? style.Value : string.Empty;
    }

    private static string TitleOf(string markup)
    {
        Match title = Title.Match(markup);

        return title.Success ? title.Groups["title"].Value.Trim() : string.Empty;
    }

    private static string VisibleText(string markup)
    {
        string withoutComments = HtmlComment.Replace(markup, string.Empty);
        string withoutStyles = StyleElement.Replace(withoutComments, string.Empty);

        return string.Join(' ', AnyTag.Replace(withoutStyles, " ")
                                      .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
