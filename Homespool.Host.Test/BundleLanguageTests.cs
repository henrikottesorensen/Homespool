using System;
using System.Globalization;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

/// <summary>
/// The instructions that leave the browser, in the language of whoever downloaded them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The last user-facing surface to be found, and it was missed for a structural reason.</b> Every
/// audit looked at what the application renders to a browser; this is a file it writes into a zip. It
/// carries twelve comment lines in the ini and a whole README, and both are read at the moment
/// somebody is standing at a printer with a USB stick — which is exactly when the page they came from
/// is no longer available to re-read.
/// </para>
/// <para>
/// <b>Three things stay English on purpose</b>, and the tests below say so rather than leaving it to
/// be re-derived: the ini's keys and section names, because firmware parses them; and the printer's
/// own menu path, because it names a screen this application does not author.
/// </para>
/// </remarks>
public sealed class BundleLanguageTests
{
    private static PrusaConnectOptions Options(bool tls = true)
    {
        return new() { PrinterHost = "printers.example.com", PrinterPort = 15443, PrinterTls = tls };
    }

    /// <summary>
    /// The ini's comments follow the reader, and its keys do not follow anybody.
    /// </summary>
    [Fact]
    public void TheIniIsCommentedInTheReadersLanguage()
    {
        string english = InCulture("en-GB", () => ConnectIni.BuildFile(Options(), "printers.example.com", "abc", TestLocaliser.Shared()));
        string danish = InCulture("da", () => ConnectIni.BuildFile(Options(), "printers.example.com", "abc", TestLocaliser.Shared()));

        english.Should().Contain("Copy this file to the root of a USB stick");
        danish.Should().Contain("Kopiér denne fil til roden af en USB-nøgle");
        danish.Should().NotContain("Copy this file");
    }

    /// <summary>
    /// The keys firmware parses are identical in both, or the file stops working when translated.
    /// </summary>
    /// <remarks>
    /// <b>This is the failure the whole machine-text boundary exists to prevent</b>, and here it would
    /// be silent: a printer given a translated key name does not complain, it resets the value to its
    /// struct default — and <c>token</c>'s default is empty, which de-enrols the printer.
    /// </remarks>
    [Fact]
    public void TheKeysFirmwareParsesAreNotTranslated()
    {
        string danish = InCulture("da", () => ConnectIni.BuildFile(Options(), "printers.example.com", "abc", TestLocaliser.Shared()));

        foreach (string key in new[] { "[service::connect]", "hostname =", "port =", "tls =", "custom_cert =", "token =" })
        {
            danish.Should().Contain(key, "firmware parses this and has never heard of Danish");
        }
    }

    /// <summary>
    /// The printer's own menu path is quoted as the printer spells it, in either language.
    /// </summary>
    [Fact]
    public void ThePrintersMenuPathIsQuotedNotTranslated()
    {
        string danish = InCulture("da", () => ConnectIni.BuildFile(Options(), "printers.example.com", "abc", TestLocaliser.Shared()));

        danish.Should().Contain("Prusa Connect -> Load Settings",
                                "it names a menu on the printer, which this application does not author");
    }

    /// <summary>
    /// The README follows the reader too, headings and troubleshooting alike.
    /// </summary>
    [Fact]
    public void TheReadmeIsWrittenInTheReadersLanguage()
    {
        string danish = InCulture(
            "da", () => ProvisioningReadme.Build(Options(), "printers.example.com", "Bænken", TestLocaliser.Shared()));

        danish.Should().Contain("Klargøringspakke til **Bænken**");
        danish.Should().Contain("Pak ud på en USB-nøgle");
        danish.Should().Contain("Hvis det ikke virker");
        danish.Should().NotContain("Unzip onto a USB stick");
    }

    /// <summary>
    /// A markdown document still has to be a markdown document after translation.
    /// </summary>
    /// <remarks>
    /// Localising per block leaves the structure in the code, which is the point: a translator writes
    /// sentences and cannot accidentally take the table apart.
    /// </remarks>
    [Fact]
    public void TheReadmeKeepsItsStructure()
    {
        string danish = InCulture(
            "da", () => ProvisioningReadme.Build(Options(), "printers.example.com", "Bænken", TestLocaliser.Shared()));

        danish.Should().StartWith("# ");
        danish.Should().Contain("|---|---|", "the tables survive translation");
        danish.Should().Contain("`prusa_printer_settings.ini`", "file names are not words");
        danish.Should().Contain("`connect.der`");
    }

    /// <summary>
    /// A plain-HTTP deployment gets the warning that belongs to it, in Danish as in English.
    /// </summary>
    [Fact]
    public void ThePlainHttpWarningIsTranslatedToo()
    {
        string danish = InCulture(
            "da", () => ConnectIni.BuildFile(Options(tls: false), "printers.example.com", "abc", TestLocaliser.Shared()));

        danish.Should().Contain("klartekst", "the reader has to understand what plain HTTP costs them");
        danish.Should().Contain("tls = False", "and the key is still the key");
    }

    /// <summary>
    /// The one file in the bundle whose name is a word gets translated; the two that are looked up
    /// by name do not.
    /// </summary>
    /// <remarks>
    /// <b>Danish computing says LÆSMIG where English says README</b>, and the person opening the zip
    /// should meet the word they know. Safe to do because nothing parses it - the printer ignores
    /// this file - and because .NET writes a non-ASCII entry name as UTF-8 with bit 11 of the
    /// general-purpose flag set, which is the standard signal every modern unzip honours. That was
    /// checked rather than assumed.
    /// </remarks>
    [Fact]
    public void OnlyTheFileNobodyParsesIsRenamed()
    {
        InCulture("en-GB", () => ProvisioningReadme.FileNameFor(TestLocaliser.Shared()))
            .Should().Be("README.Bundle.md");
        InCulture("da", () => ProvisioningReadme.FileNameFor(TestLocaliser.Shared()))
            .Should().Be("LÆSMIG.Pakke.md");

        ConnectIni.FileName.Should().Be("prusa_printer_settings.ini",
                                        "firmware looks for this by name and has never heard of Danish");
    }

    /// <summary>
    /// The README's own contents table names the file it is, whatever that is called.
    /// </summary>
    [Fact]
    public void TheContentsTableNamesTheFileItIs()
    {
        InCulture("da", () => ProvisioningReadme.Build(Options(), "printers.example.com", "Bænken", TestLocaliser.Shared()))
            .Should().Contain("`LÆSMIG.Pakke.md`").And.NotContain("README.Bundle.md");
    }

    private static T InCulture<T>(string cultureName, Func<T> body)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
