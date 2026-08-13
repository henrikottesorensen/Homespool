using System;
using System.Globalization;

using AwesomeAssertions;

using Homespool.Host.Exceptions;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// The sentence a thrown error becomes on a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exception messages were a user-facing surface nobody was treating as one.</b> Eight catch
/// sites assigned <c>e.Message</c> straight to a status message, so a refused upload explained
/// itself in the language the exception was written in — English — regardless of who was reading.
/// Three localisation audits missed it, because they searched page models and services for literals
/// and these literals live in the type being thrown.
/// </para>
/// <para>
/// What the tests below are actually guarding is the split: <see cref="Exception.Message"/> stays
/// English for the log, and the key answers in the reader's language for the page. Either half
/// looks correct on its own to an English reader, which is why both are asserted together.
/// </para>
/// </remarks>
public sealed class ErrorTextTests
{
    [Fact]
    public void AFileErrorIsSaidInTheReadersLanguage()
    {
        PrintFileNotFoundException error = new("bracket.gcode");

        Describe(error, "en-GB").Should().Be("You have no file named ‘bracket.gcode’.");
        Describe(error, "da").Should().Be("Du har ingen fil, der hedder ‘bracket.gcode’.");
    }

    /// <summary>
    /// The English message is for the log, and is not what a page shows.
    /// </summary>
    /// <remarks>
    /// Asserted because the two texts drifting apart is a maintenance risk this design accepts
    /// deliberately: one audience is a developer reading a stack trace, the other is somebody who
    /// pressed a button, and neither should be made to read the other's language.
    /// </remarks>
    [Fact]
    public void TheExceptionsOwnMessageStaysEnglishForTheLog()
    {
        PrintFileNotFoundException error = new("bracket.gcode");

        InCulture("da", () => error.Message).Should().Be("You have no file named 'bracket.gcode'.");
    }

    /// <summary>
    /// A file name is data and is reproduced, not translated.
    /// </summary>
    [Fact]
    public void WhatSomebodyTypedComesBackUnchanged()
    {
        Describe(new PrintFileNameRejectedException("Bräcket-2.stl", "fileName"), "da")
            .Should().Contain("Bräcket-2.stl");
    }

    /// <summary>
    /// The printer's own refusal reason is reproduced, inside a sentence that is translated.
    /// </summary>
    /// <remarks>
    /// <b>The frame is ours and the reason is the printer's</b>, so exactly one of the two moves.
    /// Translating the reason would attribute words to the printer it never said, and would stop
    /// matching what a support thread quotes.
    /// </remarks>
    [Fact]
    public void ThePrintersOwnWordsAreNotTranslated()
    {
        PrinterRefusedException error = new(Events.Rejected, "Not in a state to accept this");

        Describe(error, "da")
            .Should().Be("Printeren afviste den: Not in a state to accept this");
    }

    /// <summary>
    /// A refusal with no reason quotes firmware's event name, which is also not ours to translate.
    /// </summary>
    [Fact]
    public void AnEventNameIsFirmwareVocabulary()
    {
        Describe(new PrinterRefusedException(Events.Failed, reason: null), "da")
            .Should().Be("Printeren afviste den (Failed).");
    }

    /// <summary>
    /// A status inside an error sentence reads in the language of the sentence around it.
    /// </summary>
    /// <remarks>
    /// <b>The case that would have gone unnoticed.</b> Handing <c>Status.ToString()</c> to the
    /// format string produces "Printeren er Printing" — a Danish sentence with an English enum
    /// member in the middle of it, which no test asserting merely that the message is Danish would
    /// catch. Passing the enum keeps the decision where <c>PrinterStatusText</c> is.
    /// </remarks>
    [Fact]
    public void AStatusInsideAnErrorIsTranslatedToo()
    {
        PrinterBusyException error = new(PrinterStatus.Printing);

        Describe(error, "en-GB").Should().Be(
            "The printer is Printing - heaters can only be changed when the printer is not busy.");
        Describe(error, "da").Should().Be(
            "Printeren er Printer - varmelegemer kan kun ændres, når printeren ikke er optaget.");
    }

    /// <summary>
    /// Unknown is not busy, and says so with a different sentence rather than a different word.
    /// </summary>
    [Fact]
    public void AnUnknownStateSaysWaitRatherThanStop()
    {
        Describe(new PrinterBusyException(PrinterStatus.Unknown), "da")
            .Should().StartWith("Printerens tilstand kendes ikke endnu");
    }

    /// <summary>
    /// An exception that has not opted in still says something, rather than nothing.
    /// </summary>
    /// <remarks>
    /// The fallback is the exception's own message, because a silent page is worse than an English
    /// one — and because it means adding the interface to one more type is the whole change, with no
    /// call site to revisit.
    /// </remarks>
    [Fact]
    public void AnErrorWithNoKeyFallsBackToItsOwnMessage()
    {
        Describe(new InvalidOperationException("Something specific went wrong."), "da")
            .Should().Be("Something specific went wrong.");
    }

    private static string Describe(Exception error, string cultureName)
    {
        return InCulture(cultureName, () => TestLocaliser.Errors().For(error));
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
