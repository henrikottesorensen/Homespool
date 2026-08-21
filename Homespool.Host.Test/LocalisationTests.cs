using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// That the localisation pipeline actually switches, rather than merely being wired.
/// </summary>
/// <remarks>
/// <para>
/// <b>The localiser is resolved from a real container against the real <c>.resx</c> files</b>, not
/// substituted. A stubbed <c>IStringLocalizer</c> would pass whether or not the resources were
/// embedded, named correctly or found at runtime — which is the whole class of failure this
/// infrastructure can have, and the only one worth a test.
/// </para>
/// <para>
/// <b>Culture is set explicitly rather than through a request.</b> These assert the resolution and
/// resource layers; the middleware that picks a culture from a request is a separate concern and one
/// these do not reach.
/// </para>
/// </remarks>
public sealed class LocalisationTests
{
    /// <summary>
    /// A localiser reading the shipped resources, exactly as the application resolves one.
    /// </summary>
    private static IStringLocalizer<SharedResource> Localiser()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddLocalization();

        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    /// <summary>
    /// Runs an assertion with both culture axes set, and puts them back afterwards.
    /// </summary>
    private static void InCulture(string cultureName, Action assertion)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            assertion();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>
    /// The one that would catch the pipeline never switching: the same call, two languages, two
    /// answers.
    /// </summary>
    [Fact]
    public void AStatusReadsInTheCurrentLanguage()
    {
        PrinterStatusText text = new(Localiser());

        InCulture("en-GB", () => text.For(PrinterStatus.Attention).Should().Be("Waiting for you"));
        InCulture("da", () => text.For(PrinterStatus.Attention).Should().Be("Venter på dig"));
    }

    /// <summary>
    /// Null and the two "nothing said yet" states are one thing to a reader, in either language.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(PrinterStatus.Undefined)]
    [InlineData(PrinterStatus.Unknown)]
    public void APrinterThatHasNotReportedIsJustConnected(PrinterStatus? status)
    {
        PrinterStatusText text = new(Localiser());

        InCulture("en-GB", () => text.For(status).Should().Be("Connected"));
        InCulture("da", () => text.For(status).Should().Be("Forbundet"));
    }

    /// <summary>
    /// A state nobody wrote a word for falls back to the enum's own name rather than throwing - the
    /// behaviour the hand-written switch had, kept deliberately.
    /// </summary>
    /// <remarks>
    /// <see cref="PrinterStatus.Manipulating"/> is the real case: it has no resource because no
    /// firmware can report it, so it exercises the fallback as an actual enum member rather than as
    /// a cast integer. The cast is kept beside it for the other shape - a state a future firmware
    /// adds that this build has never heard of.
    /// </remarks>
    [Fact]
    public void AStatusWithNoResourceKeepsItsOwnName()
    {
        PrinterStatusText text = new(Localiser());

        InCulture("en-GB", () =>
        {
            text.For(PrinterStatus.Manipulating).Should().Be("Manipulating");
            text.For((PrinterStatus)999).Should().Be("999");
        });

        InCulture("da", () => text.For(PrinterStatus.Manipulating).Should().Be("Manipulating"));
    }

    /// <summary>
    /// Both languages take two forms, and the count is formatted into the right one.
    /// </summary>
    [Fact]
    public void ACountPicksItsPluralForm()
    {
        IStringLocalizer<SharedResource> localiser = Localiser();

        InCulture("en-GB", () =>
        {
            Plural.Format(localiser, "Printers", 1).Should().Be("1 printer");
            Plural.Format(localiser, "Printers", 2).Should().Be("2 printers");
            Plural.Format(localiser, "Printers", 0).Should().Be("0 printers");
        });

        InCulture("da", () =>
        {
            Plural.Format(localiser, "Printers", 1).Should().Be("1 printer");
            Plural.Format(localiser, "Printers", 2).Should().Be("2 printere");
        });
    }

    /// <summary>
    /// The count is rendered by the culture, not pasted in - which is the half of localisation that
    /// has nothing to do with resource files.
    /// </summary>
    [Fact]
    public void ACountIsFormattedByItsCulture()
    {
        IStringLocalizer<SharedResource> localiser = Localiser();

        InCulture("en-GB", () => Plural.Format(localiser, "Files", 1234).Should().Be("1,234 files"));
        InCulture("da", () => Plural.Format(localiser, "Files", 1234).Should().Be("1.234 filer"));
    }

    /// <summary>
    /// A key Danish has not translated falls back to English rather than failing, which is what lets
    /// Phase B convert one page at a time.
    /// </summary>
    [Fact]
    public void AnUntranslatedKeyFallsBackRatherThanBreaking()
    {
        IStringLocalizer<SharedResource> localiser = Localiser();

        // Present in the neutral file with an explanatory comment, and deliberately not in da.
        InCulture("da", () =>
        {
            LocalizedString found = localiser["PrinterStatus_Idle"];
            found.ResourceNotFound.Should().BeFalse();
            found.Value.Should().Be("Ikke optaget");
        });

        InCulture("da", () =>
        {
            LocalizedString missing = localiser["Language_Heading"];
            missing.Value.Should().Be("Sprog");
        });
    }

    /// <summary>
    /// <c>en-GB</c> is a real culture and <c>en-UK</c> is not, and the difference is invisible until
    /// something is formatted - measured, because ICU invents the second rather than refusing it.
    /// </summary>
    /// <remarks>
    /// The guard that matters: it is exactly the mistake somebody makes "correcting" the default,
    /// and <c>GetCultureInfo("en-UK")</c> does not throw. Its dates come out in the American order
    /// while its English name still reads "English (United Kingdom)".
    /// </remarks>
    [Fact]
    public void TheDefaultCultureIsTheOneThatFormatsBritishDates()
    {
        DateTime ninthOfMarch = new(2026, 3, 9);

        CultureInfo shipped = CultureInfo.GetCultureInfo(SupportedLanguages.DefaultCulture);
        shipped.CultureTypes.Should().NotHaveFlag(CultureTypes.UserCustomCulture);
        ninthOfMarch.ToString("d", shipped).Should().Be("09/03/2026");

        CultureInfo invented = CultureInfo.GetCultureInfo("en-UK");
        invented.CultureTypes.Should().HaveFlag(CultureTypes.UserCustomCulture);
        ninthOfMarch.ToString("d", invented).Should().Be("3/9/2026");
    }

    /// <summary>
    /// What a browser asks for, and what it selects.
    /// </summary>
    /// <remarks>
    /// In order: the three shipped names exactly; a specific culture selecting the neutral language
    /// shipped for it; plain <c>en</c> selecting <c>en-GB</c> rather than <c>en-US</c>, which is
    /// decided by list order and is the direction a one-way prefix check silently gets wrong; and
    /// three that match nothing, including one that merely shares a prefix.
    /// </remarks>
    [Theory]
    [InlineData("en-GB", "en-GB")]
    [InlineData("en-US", "en-US")]
    [InlineData("da", "da")]
    [InlineData("da-DK", "da")]
    [InlineData("en", "en-GB")]
    [InlineData("de-DE", null)]
    [InlineData("dan", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ARequestedCultureSelectsWhatIsActuallyShipped(string? requested, string? expected)
    {
        SupportedLanguages.Resolve(requested).Should().Be(expected);
        SupportedLanguages.IsSupported(requested).Should().Be(expected is not null);
    }

    /// <summary>
    /// What <c>en-US</c> is actually for: the same words, formatted the American way.
    /// </summary>
    /// <remarks>
    /// It ships with no resource file, so this is also the assertion that a culture with no
    /// resources of its own still reads the neutral ones rather than falling back to nothing. If
    /// somebody later adds an <c>en-US</c> file, the first half of this test is what says the words
    /// were supposed to be identical.
    /// </remarks>
    [Fact]
    public void AmericanEnglishSharesTheWordsAndNotTheFormatting()
    {
        PrinterStatusText text = new(Localiser());
        DateTime ninthOfMarch = new(2026, 3, 9);

        InCulture("en-US", () =>
        {
            text.For(PrinterStatus.Attention).Should().Be("Waiting for you");
            ninthOfMarch.ToString("d", CultureInfo.CurrentCulture).Should().Be("3/9/2026");
        });

        InCulture("en-GB", () =>
        {
            text.For(PrinterStatus.Attention).Should().Be("Waiting for you");
            ninthOfMarch.ToString("d", CultureInfo.CurrentCulture).Should().Be("09/03/2026");
        });
    }

    /// <summary>
    /// The picker's labels are a function of the date, and this pins both sides of it.
    /// </summary>
    /// <remarks>
    /// Asserted against fixed dates rather than the clock, so it neither depends on the calendar nor
    /// quietly stops being covered for most of the year.
    /// </remarks>
    [Fact]
    public void LabelsAreResolvedForTheDayTheyAreShown()
    {
        IReadOnlyDictionary<string, string> ordinary =
            SupportedLanguages.DisplayNamesOn(new DateTimeOffset(2026, 3, 31, 23, 59, 0, TimeSpan.Zero));

        ordinary[SupportedLanguages.DefaultCulture].Should().Be("English (UK)");
        ordinary["en-US"].Should().Be("English (US)");

        IReadOnlyDictionary<string, string> april =
            SupportedLanguages.DisplayNamesOn(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero));

        april[SupportedLanguages.DefaultCulture].Should().Be("English (Traditional)");
        april["en-US"].Should().Be("English (Simplified)");
        april["da"].Should().Be("Dansk");
    }

    /// <summary>
    /// Whatever a label says, the value behind it is a culture name and does not move.
    /// </summary>
    /// <remarks>
    /// The assertion that matters: the offered cultures, their order and what <c>Resolve</c> returns
    /// are identical whichever labels are in force, so choosing a language stores the same thing on
    /// any day of the year. A label wired deeper than the label would be a bug.
    /// </remarks>
    [Fact]
    public void LabelsNeverChangeWhatIsStored()
    {
        DateTimeOffset april = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

        SupportedLanguages.DisplayNamesOn(april).Keys
                          .Should().BeEquivalentTo(SupportedLanguages.DisplayNames.Keys);

        SupportedLanguages.Resolve("en-US").Should().Be("en-US");
        SupportedLanguages.CultureNames.Should().Equal("en-GB", "en-US", "da");
    }

    /// <summary>
    /// Every English string has a Danish one, and both files are well-formed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A key added to one file and not the other is invisible until somebody reads that page in
    /// Danish</b>, and Phase B adds them in batches - which is exactly the shape of drift that
    /// accumulates quietly. This compares the two files directly rather than going through the
    /// localiser, because the localiser's fallback is what would hide the gap.
    /// </para>
    /// <para>
    /// It also parses both as XML, which catches the one mistake a resource file invites: writing an
    /// HTML entity into it. <c>&amp;larr;</c> is not one of XML's five, and it took the build down
    /// with a stack trace naming neither the file's line nor the entity.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryEnglishStringHasADanishOne()
    {
        IReadOnlyDictionary<string, string> english = ReadResources("SharedResource.resx");
        IReadOnlyDictionary<string, string> danish = ReadResources("SharedResource.da.resx");

        english.Should().NotBeEmpty("the neutral file is what everything falls back to");

        string[] untranslated = english.Keys.Except(danish.Keys).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        untranslated.Should().BeEmpty("every shipped string is translated, so no page is half Danish");

        string[] orphaned = danish.Keys.Except(english.Keys).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        orphaned.Should().BeEmpty("a Danish key with no English one can never be reached");
    }

    /// <summary>
    /// American English overrides only what actually differs, and nothing it overrides is invented.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The opposite assertion to the Danish one, deliberately.</b> Danish must be complete, since
    /// a gap there leaves a page half English. <c>en-US</c> must be <i>nearly empty</i>: it shares
    /// almost every string with <c>en-GB</c>, and a file holding copies would only give the next
    /// edit somewhere to update one and not the other. So this asserts smallness rather than
    /// completeness.
    /// </para>
    /// <para>
    /// The bound is a smell test rather than a rule. If American English ever needs more than a
    /// handful of strings, something has been copied that should have been shared, and this failing
    /// is the cheapest place to notice.
    /// </para>
    /// </remarks>
    [Fact]
    public void AmericanEnglishOverridesOnlyWhatDiffers()
    {
        IReadOnlyDictionary<string, string> english = ReadResources("SharedResource.resx");
        IReadOnlyDictionary<string, string> american = ReadResources("SharedResource.en-US.resx");

        american.Keys.Except(english.Keys).Should()
                .BeEmpty("an en-US key with no neutral one overrides nothing");

        american.Should().HaveCountLessThan(10,
            "en-US exists for the strings that genuinely differ, not as a copy of the neutral file");

        foreach ((string key, string value) in american)
        {
            value.Should().NotBe(english[key], $"{key} is in en-US only because it differs");
        }
    }

    /// <summary>
    /// The one string that differs, doing what it exists for.
    /// </summary>
    /// <remarks>
    /// A reader in the United States does not assume Celsius, and 215 °F is a plausible-looking bed
    /// temperature and a wrong nozzle one - so the unit is stated there and left off where the
    /// assumption is safe.
    /// <para>
    /// <b>The key moved when the printer page was rebuilt around a status card.</b> It was
    /// <c>Printers_TemperatureTarget</c>, a whole "215.0 / target 215.0" line, and the card shows the
    /// two numbers in separate places - so the unit rule now lives on <c>Printers_Degrees</c>, which
    /// every one of them is rendered through. Carrying the override across mattered more than the
    /// wording it used to sit in.
    /// </para>
    /// </remarks>
    [Fact]
    public void AmericanEnglishStatesTheTemperatureUnit()
    {
        IStringLocalizer<SharedResource> localiser = Localiser();

        InCulture("en-US", () => localiser["Printers_Degrees", "215"].Value.Should().Be("215 °C"));

        InCulture("en-GB", () => localiser["Printers_Degrees", "215"].Value.Should().Be("215°"));

        InCulture("da", () => localiser["Printers_Degrees", "215"].Value.Should().Be("215°"));
    }

    /// <summary>
    /// Two keys holding the same English sentence, which is how a corrected string gets uncorrected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written after exactly that happened.</b> Henrik corrected "That printer no longer exists."
    /// to <c>Denne printer findes ikke længere.</c> in round one. In round two I added a second key
    /// carrying the same English, translated it again, and got it wrong again — and nothing noticed,
    /// because both keys had a Danish value and the parity test only asks whether one exists. The
    /// two call sites were in the same file.
    /// </para>
    /// <para>
    /// <b>The allowlist is the point, not an escape hatch.</b> Some duplicates are legitimate: a nav
    /// label and a page title say the same words today and may not tomorrow, and separating them is
    /// what allows that. Each entry here is a decision that they should be able to diverge. Adding
    /// one because a test went red is how this stops working.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoTwoKeysCarryTheSameEnglish()
    {
        // Pairs that may say the same thing today and diverge later. Each is deliberate.
        string[] sanctioned =
        [
            "Actions",                  // a table heading and a shared label
            "Created",                  // ditto
            "Save",                     // a form button and the language picker's own
            "Profile",                  // the nav entry and the page's heading
            "Camera streaming",         // ditto - Nav_LiveView and LiveView_Title
            "Manage your account",      // the nav tooltip and the page title
            "Printer certificate",      // the nav tooltip and the page title
            "Resend email confirmation", // the link and the page it leads to
            "Workshop",                 // an example team name and an example camera name
            "That printer is still processing a previous command.", // a page message and an exception

            // A verb and a noun that English spells identically. Files_Queue is the button that puts
            // a file in the queue (da: "Sæt i kø"); Common_Queue is the heading over one (da: "Kø").
            // Danish had to tell them apart and did. The English arguably should too - "Add to queue"
            // on the button - which is a UI change rather than a translation one.
            "Queue",

        ];

        IReadOnlyDictionary<string, string> english = ReadResources("SharedResource.resx");

        List<string> collisions = english.GroupBy(entry => entry.Value.Trim(), StringComparer.Ordinal)
                                .Where(group => group.Count() > 1)
                                .Where(group => !sanctioned.Contains(group.Key, StringComparer.Ordinal))
                                .Select(group => $"“{group.Key}” is on {string.Join(" and ", group.Select(e => e.Key))}")
                                .ToList();

        collisions.Should().BeEmpty(
            "two keys with one sentence drift apart in translation - merge them, or add the pair to "
            + "the sanctioned list with a reason");
    }

    /// <summary>
    /// A key nothing names is a sentence nobody reads, translated at somebody's expense.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written after finding three.</b> <c>TwoFactor_ScanQr</c> and
    /// <c>TwoFactor_LoseCodesWarning</c> were superseded when their pages were reworded;
    /// <c>Cert_ToTakeEffect</c> — <i>"to take effect -"</i> — was half of a sentence, left behind when
    /// the two halves were merged into one key. All three were fully translated, and a translator
    /// coming to this file next would have had no way to tell they were dead.
    /// </para>
    /// <para>
    /// <b>Three families are named rather than searched for, and cannot be found by this.</b>
    /// <see cref="PrinterStatusText"/> builds its keys from a prefix and an enum member, and
    /// <see cref="Plural"/> from a prefix and One/Other - so their keys appear nowhere as literals.
    /// They are matched by shape below. A third such family would need adding here, which is the
    /// price of constructing key names at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryKeyIsNamedBySomething()
    {
        string root = SourceRoot();

        string code = string.Concat(
            Directory.EnumerateFiles(Path.Combine(root, "Homespool.Host"), "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "Homespool.Host"), "*.cshtml", SearchOption.AllDirectories))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                     .Select(File.ReadAllText));

        // Built from a prefix at run time, so they are never written out in full anywhere.
        // Capability_ is CapabilityText, which names a capability from the enum member - the same
        // seam, and the reason a grep for the key finds nothing. QueueStatus_ is the fourth, from
        // DetailModel.QueueStatusText over QueueEntryStatus - the "third such family" this test's
        // remarks predicted would have to be added by hand.
        string[] constructed = ["PrinterStatus_", "Intent_", "Capability_", "QueueStatus_"];
        string[] constructedSuffixes = ["_One", "_Other"];

        List<string> orphans = ReadResources("SharedResource.resx")
                               .Keys
                               .Where(key => !constructed.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
                               .Where(key => !constructedSuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal)))
                               .Where(key => !code.Contains($"\"{key}\"", StringComparison.Ordinal))
                               .ToList();

        orphans.Should().BeEmpty(
            "a key nothing names is dead weight that still gets translated - delete it, or find the "
            + "page that lost it");
    }

    /// <summary>The repository root, walked up from the test binary.</summary>
    /// <remarks>
    /// These tests read the source tree rather than the compiled assembly, because what they are
    /// checking is the files - a resource baked into a satellite assembly has already lost the
    /// distinction between "absent" and "empty".
    /// </remarks>
    private static string SourceRoot()
    {
        string directory = AppContext.BaseDirectory;

        while (directory is not null && !Directory.Exists(Path.Combine(directory, "Homespool.Host", "Localisation")))
        {
            directory = Path.GetDirectoryName(directory)!;
        }

        directory.Should().NotBeNull("the tests run from inside the repository");

        return directory!;
    }

    /// <summary>
    /// Reads a resource file from the source tree rather than the compiled assembly, which is what
    /// lets this compare the two files as files.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadResources(string fileName)
    {
        string path = Path.Combine(SourceRoot(), "Homespool.Host", "Localisation", fileName);
        File.Exists(path).Should().BeTrue($"{fileName} is where the strings live");

        return XDocument.Load(path)
                        .Root!
                        .Elements("data")
                        .ToDictionary(
                            element => element.Attribute("name")!.Value,
                            element => element.Element("value")?.Value ?? string.Empty,
                            StringComparer.Ordinal);
    }

    /// <summary>
    /// Running as another culture must not leak, or the next thing on the thread inherits it.
    /// </summary>
    /// <remarks>
    /// This is the failure a background email sender would cause: composing one message in Danish
    /// and leaving every later one on that thread Danish too, for recipients who never asked.
    /// </remarks>
    [Fact]
    public void ComposingInAnotherCulturePutsTheThreadBack()
    {
        InCulture("en-GB", () =>
        {
            string danish = UserCultures.InCulture("da", () => 1234.5.ToString("N1", CultureInfo.CurrentCulture));

            danish.Should().Be("1.234,5");
            CultureInfo.CurrentCulture.Name.Should().Be("en-GB");
            CultureInfo.CurrentUICulture.Name.Should().Be("en-GB");
        });
    }

    /// <summary>
    /// An account with no stored language is not an account that chose English, so composing for it
    /// changes nothing.
    /// </summary>
    [Fact]
    public void NoStoredLanguageLeavesTheCultureAlone()
    {
        InCulture("en-GB", () =>
        {
            UserCultures.InCulture(null, () => CultureInfo.CurrentCulture.Name).Should().Be("en-GB");
            UserCultures.InCulture("de-DE", () => CultureInfo.CurrentCulture.Name).Should().Be("en-GB");
        });
    }
}
