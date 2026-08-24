using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.Certificates;
using Homespool.Host.Exceptions;
using Homespool.Host.Localisation;
using Homespool.Host.Printing;
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
        PrinterRefusedException error = new(PrinterEventType.Rejected, "Not in a state to accept this");

        Describe(error, "da")
            .Should().Be("Printeren afviste den: Not in a state to accept this");
    }

    /// <summary>
    /// A refusal with no reason quotes firmware's event name, which is also not ours to translate.
    /// </summary>
    [Fact]
    public void AnEventNameIsFirmwareVocabulary()
    {
        Describe(new PrinterRefusedException(PrinterEventType.Failed, reason: null), "da")
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
            "The printer is Printing - this can only be done when the printer is not busy.");
        Describe(error, "da").Should().Be(
            "Printeren er Printer - dette kan kun gøres, når printeren ikke er optaget.");
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
    /// A refusal carrying no reason is read as "busy", because on this protocol it can mean nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The empty string is the information, not the absence of it.</b> Firmware's
    /// <c>Planner::command</c> returns at the <c>background_command</c> guard before dispatching to
    /// any overload, and that guard is the only rejection built without a reason - so
    /// <i>"Processing other command"</i> cannot be what a busy printer says, and a reason-less
    /// <c>Rejected</c> cannot be anything but one.
    /// </para>
    /// <para>
    /// <b><c>Failed</c> is deliberately excluded</b>, and asserted here so the two do not merge: a
    /// reason-less failure says nothing about a busy printer, and lending it this sentence would
    /// invent an explanation.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARefusalWithNoReasonSaysThePrinterIsBusy()
    {
        Describe(new PrinterRefusedException(PrinterEventType.Rejected, reason: null), "en-GB")
            .Should().StartWith("The printer is still running a previous command");

        Describe(new PrinterRefusedException(PrinterEventType.Failed, reason: null), "en-GB")
            .Should().Contain("Failed", "a failure is not a busy printer, and must not borrow its sentence");

        Describe(new PrinterRefusedException(PrinterEventType.Rejected, "No print to pause"), "en-GB")
            .Should().Contain("No print to pause", "the printer's own words win whenever it gives any");
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

    /// <summary>
    /// A sentence a service chose, said in the reader's language.
    /// </summary>
    /// <remarks>
    /// The same journey without a throw. <c>CameraService</c> and <c>CameraSourcePolicy</c> used to
    /// write finished English into their return values; they now name a key, which is only an
    /// improvement if something checks the key resolves.
    /// </remarks>
    [Fact]
    public void AServicesChosenSentenceIsSaidInTheReadersLanguage()
    {
        MessageKey chosen = MessageKey.For("Cameras_SourceScheme", "ftp");

        InCulture("en-GB", () => TestLocaliser.Errors().For(chosen))
            .Should().Be("Homespool does not read cameras over ftp. Use rtsp, rtsps, http, https, rtmp or onvif.");
        InCulture("da", () => TestLocaliser.Errors().For(chosen))
            .Should().Be("Homespool kan ikke læse kameraer over ftp. Brug rtsp, rtsps, http, https, rtmp eller onvif.");
    }

    /// <summary>
    /// Every key a service or a classifier can name has words behind it, in both languages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this design introduces.</b> Moving a sentence out of a service and into a key
    /// means the two can now be separated: rename the key, or add an arm to
    /// <c>PrinterAddressSuggestion.NoteKey</c> without adding words, and the page renders
    /// <c>Cameras_NotYourTeam</c> at somebody. Nothing else in the suite would notice, because the
    /// page still returns 200 and the parity test only compares the two resource files with each
    /// other — both would be equally missing it.
    /// </para>
    /// <para>
    /// Listed by hand rather than discovered, deliberately. A test that reflects over the keys a
    /// codebase happens to contain asserts that what is there is there.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryKeyAServiceCanNameHasWordsBehindIt()
    {
        string[] keys =
        [
            "Cameras_SourceMissing", "Cameras_SourceIncomplete", "Cameras_SourceScheme",
            "Cameras_SourceIsThisServer", "Cameras_NotFoundOrNotYours",
            "Cameras_AttachedNeedsAdministrator", "Cameras_NotYourTeam", "Cameras_StreamServerRefused",
            "Cameras_NoPictureLocal", "Cameras_NoPictureNetwork",
            "Bundle_AddressSurvivesLease", "Bundle_AddressIsTheContainers",
            "Bundle_AddressUntilLeaseMoves", "Bundle_AddressUnclassified",
        ];

        foreach (string key in keys)
        {
            foreach (string culture in new[] { "en-GB", "da" })
            {
                InCulture(culture, () => TestLocaliser.Shared()[key])
                    .ResourceNotFound.Should().BeFalse($"{key} is named in code and must have words in {culture}");
            }
        }
    }

    /// <summary>
    /// Every durability a classifier can produce says something.
    /// </summary>
    /// <remarks>
    /// Drives the mapping from the enum rather than from the key list above, so adding a member
    /// without adding words fails here rather than on somebody's setup page.
    /// </remarks>
    [Fact]
    public void EveryAddressDurabilityHasANote()
    {
        foreach (AddressDurability durability in Enum.GetValues<AddressDurability>())
        {
            PrinterAddressSuggestion suggestion = new("192.168.1.5", durability);

            InCulture("da", () => TestLocaliser.Shared()[suggestion.NoteKey])
                .ResourceNotFound.Should().BeFalse($"{durability} is reachable and needs words");
        }
    }

    /// <summary>
    /// An intent is named for a person, not for a log.
    /// </summary>
    /// <remarks>
    /// <b><see cref="IPrinterIntent.Name"/> is the type name and its own documentation says so</b> -
    /// "for logs and failure bodies". It was reaching a status message, so the page told somebody
    /// "PausePrint sent." and a Danish reader "PausePrint er afsendt." Before the vocabulary refactor
    /// it showed <c>PAUSE_PRINT</c> instead, which is the same defect in a different spelling.
    /// </remarks>
    [Fact]
    public void AnIntentIsNamedForAPersonRatherThanForALog()
    {
        IPrinterIntent intent = new PausePrint();

        intent.Name.Should().Be("PausePrint", "that is what the seam exists to keep off the page");

        InCulture("en-GB", () => Intents().For(intent)).Should().Be("Pause");
        InCulture("da", () => Intents().For(intent)).Should().Be("Pause");
        InCulture("da", () => Intents().For(new ResumePrint())).Should().Be("Fortsæt");
    }

    /// <summary>
    /// Every intent has words, in both languages.
    /// </summary>
    /// <remarks>
    /// Driven from the interface's implementations rather than a list, so an intent added later fails
    /// here instead of quietly falling back to its type name on somebody's printer page. The fallback
    /// exists so that failure is cosmetic rather than a rendered resource key; this is what keeps it
    /// theoretical.
    /// </remarks>
    [Fact]
    public void EveryIntentHasWords()
    {
        IEnumerable<Type> intents = typeof(IPrinterIntent).Assembly
                                                          .GetTypes()
                                                          .Where(type => typeof(IPrinterIntent).IsAssignableFrom(type))
                                                          .Where(type => !type.IsInterface && !type.IsAbstract);

        foreach (Type type in intents)
        {
            foreach (string culture in new[] { "en-GB", "da" })
            {
                InCulture(culture, () => TestLocaliser.Shared()["Intent_" + type.Name])
                    .ResourceNotFound.Should().BeFalse($"{type.Name} is shown to somebody in {culture}");
            }
        }
    }

    private static PrinterIntentText Intents()
    {
        return new PrinterIntentText(TestLocaliser.Shared());
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
