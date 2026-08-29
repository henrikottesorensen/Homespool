using System;
using System.Linq;
using System.Text.Json;

using AwesomeAssertions;

using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Host.Telemetry;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// Why a printer is waiting: lifting the code off a <c>STATE_CHANGED</c>, and turning it into a
/// sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes was found by looking at the page</b> (Henrik, 2026-08-29): a printer
/// showed a red "Waiting for you" and the page could not say what for. The reason was already in
/// the database - stored verbatim in the event's payload, where only a SQL query could reach it.
/// </para>
/// <para>
/// The cases below are the ones where being wrong is silent: a reason that outlives its dialog
/// explains the wrong screen, and a reason invented for an unknown code is worse than none.
/// </para>
/// </remarks>
public class PrinterAttentionReasonTests
{
    /// <summary>An attention as firmware sends it: a code, and nothing else.</summary>
    private const string Runout = """{"code":"23829","buttons":[]}""";

    private static PrinterEventRecord Map(string data,
                                          PrinterEventType type = PrinterEventType.StateChanged,
                                          string status = "ATTENTION")
    {
        using JsonDocument parsed = JsonDocument.Parse(data);

        return PrusaTelemetryMapping.ToRecord(
            new EventDTO { EventType = type, Status = status, Data = parsed.RootElement.Clone() },
            identity: null);
    }

    /// <summary>The code is lifted off the event, parsed, and kept as the wire spelled it.</summary>
    [Fact]
    public void AnAttentionCodeIsLiftedFromTheEvent()
    {
        PrinterEventRecord record = Map(Runout);

        record.Attention.Should().NotBeNull();
        record.Attention!.Code.Should().Be(23829, "the model prefix is part of what the printer reported");
        record.Attention.Text.Should().BeNull("an ordinary attention sends no words of its own");
    }

    /// <summary>
    /// <b>A state change with no dialog clears the reason rather than leaving it.</b>
    /// </summary>
    /// <remarks>
    /// This is the case that keeps a reason from outliving its dialog. Firmware re-sends
    /// <c>STATE_CHANGED</c> throughout an attention and again when it ends, so a null update on the
    /// ending change is what erases the explanation - without it, the next screen would be
    /// described by the last one's code.
    /// </remarks>
    [Fact]
    public void AStateChangeWithoutADialogCarriesNoReason()
    {
        Map("""{}""", status: "PRINTING").Attention.Should().BeNull();
    }

    /// <summary>
    /// Only state changes carry a dialog, so nothing is lifted from the events that do not.
    /// </summary>
    /// <remarks>
    /// Exhaustive rather than a sample: any event type that started producing an attention update
    /// would also start clearing one on every arrival, since a null update means "no dialog".
    /// </remarks>
    [Theory]
    [InlineData(PrinterEventType.JobInfo)]
    [InlineData(PrinterEventType.FileInfo)]
    [InlineData(PrinterEventType.Info)]
    [InlineData(PrinterEventType.Rejected)]
    [InlineData(PrinterEventType.Finished)]
    public void OnlyStateChangesCarryAReason(PrinterEventType type)
    {
        Map(Runout, type).Attention.Should().BeNull();
    }

    /// <summary>
    /// The printer's own words win over its code - the red-screen case, where firmware fills in a
    /// title and text an attention would not.
    /// </summary>
    [Fact]
    public void ThePrintersOwnWordsArePreferredToItsCode()
    {
        PrinterEventRecord record = Map(
            """{"code":"23501","title":"MODULAR BED ERROR","text":"Heatbed tile no. 3: overcurrent"}""",
            status: "ERROR");

        record.Attention!.Text.Should().Be("Heatbed tile no. 3: overcurrent",
                                           "a catalogue can be out of date about a machine; the machine cannot");
        record.Attention.Code.Should().Be(23501, "the code is still what the event reported");
    }

    /// <summary>A code that will not parse is dropped, not stored as something unreadable.</summary>
    [Fact]
    public void AnUnparseableCodeIsDropped()
    {
        Map("""{"code":"not-a-number"}""").Should().Match<PrinterEventRecord>(r => r.Attention == null);
    }

    /// <summary>
    /// <b>The same fault from two models decodes to the same sentence</b>, which is the whole
    /// reason the prefix is stripped rather than matched.
    /// </summary>
    [Theory]
    [InlineData(23829)] // MK3.5
    [InlineData(31829)] // Core One
    [InlineData(13829)] // MINI
    public void TheModelPrefixDoesNotChangeTheSentence(int code)
    {
        PrinterErrorText.For(code).Should().Be("Please replace filament.");
    }

    /// <summary>
    /// <b>A reader is told in their own language</b>, from firmware's own catalogue - so the page
    /// and the machine's screen say the same thing in the same words.
    /// </summary>
    [Theory]
    [InlineData("da", "Udskift filamentet.")]
    [InlineData("de", "Bitte Filament ersetzen.")]
    [InlineData("cs", "Prosím vyměňte filament.")]
    [InlineData("en", "Please replace filament.")]
    public void TheSentenceIsTranslated(string language, string expected)
    {
        PrinterErrorText.For(23829, language).Should().Be(expected);
    }

    /// <summary>
    /// A language nobody has translated falls back to English - which is what the printer's own
    /// screen does too, so the two still agree.
    /// </summary>
    [Theory]
    [InlineData("nb")]
    [InlineData("zz")]
    [InlineData(null)]
    public void AnUntranslatedLanguageFallsBackToEnglish(string? language)
    {
        PrinterErrorText.For(23829, language).Should().Be("Please replace filament.");
    }

    /// <summary>
    /// <b>Danish is ours and has to be complete</b>, because Prusa ship none: an untranslated code
    /// would silently show English to the one audience this table exists for.
    /// </summary>
    [Fact]
    public void DanishCoversEveryCodeEnglishHas()
    {
        foreach (int code in Enumerable.Range(800, 60))
        {
            string? english = PrinterErrorText.For(code, "en");

            if (english is not null)
            {
                PrinterErrorText.For(code, "da").Should().NotBe(english,
                    $"code {code} is in the catalogue and should have Danish of its own");
            }
        }
    }

    /// <summary>The printer's own words are passed through, not translated over.</summary>
    [Fact]
    public void WordsFromThePrinterAreNotReplacedByTheCatalogue()
    {
        AttentionRules.Reason(PrinterStatus.Error, 23829, "Heatbed tile no. 3", "da")
                      .Should().Be("Heatbed tile no. 3",
                                   "there is no field saying what language it arrived in, so it is passed as sent");
    }

    /// <summary>An unknown code yields no sentence rather than an invented one.</summary>
    [Fact]
    public void AnUnknownCodeHasNoSentence()
    {
        PrinterErrorText.For(23999).Should().BeNull();
        PrinterErrorText.For(null).Should().BeNull();
    }

    /// <summary>
    /// <b>Nothing is explained about a printer that is not waiting</b> - exhaustive over the
    /// states, because the stored code outlives its dialog by design until the next state change
    /// clears it, and every one of these is a badge the sentence must not appear under.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotWaiting))]
    public void OnlyAWaitingPrinterExplainsItself(PrinterStatus status)
    {
        AttentionRules.Reason(status, 23829, text: null).Should().BeNull();
    }

    /// <summary>Every state that is not the printer asking for somebody.</summary>
    public static TheoryData<PrinterStatus> NotWaiting()
    {
        TheoryData<PrinterStatus> data = [];

        foreach (PrinterStatus status in Enum.GetValues<PrinterStatus>())
        {
            if (status is not (PrinterStatus.Attention or PrinterStatus.Error))
            {
                data.Add(status);
            }
        }

        return data;
    }

    /// <summary>And a waiting printer does, from whichever source has it.</summary>
    [Theory]
    [InlineData(PrinterStatus.Attention)]
    [InlineData(PrinterStatus.Error)]
    public void AWaitingPrinterExplainsItself(PrinterStatus status)
    {
        AttentionRules.Reason(status, 23829, text: null).Should().Be("Please replace filament.");
        AttentionRules.Reason(status, 23829, "Heatbed tile no. 3").Should().Be("Heatbed tile no. 3",
            "the printer's own words beat the catalogue");
        AttentionRules.Reason(status, code: null, text: null).Should().BeNull(
            "a dialog the printer declined to explain is not explained");
        AttentionRules.Reason(status, 23829, "   ").Should().Be("Please replace filament.",
            "whitespace is absence on this wire, not the printer's words");
    }
}
