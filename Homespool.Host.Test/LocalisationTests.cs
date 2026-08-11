using System;
using System.Collections.Generic;
using System.Globalization;

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
