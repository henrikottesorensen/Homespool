using System.Collections.Generic;

using AwesomeAssertions;

using Homespool.Host.PrintFiles;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Comparing what a file was sliced for against the printer it is aimed at.
/// </summary>
/// <remarks>
/// <para>
/// <b>The silences matter as much as the findings.</b> Two of these rules hold a queue, so a rule
/// that fires when it should not is expensive - and one that fires on ordinary uploads is worse
/// than expensive, because it teaches people to click through the one that mattered. Hence a case
/// per missing half.
/// </para>
/// <para>
/// <b>Nothing here composes a sentence.</b> The comparison yields a vocabulary and the wording is a
/// resource key chosen from it, so these assert findings rather than text.
/// </para>
/// </remarks>
public class PrintFileCompatibilityTests
{
    /// <summary>
    /// Abrasive filament through a soft nozzle: the one that costs hardware, and the reason there
    /// is a hold at all.
    /// </summary>
    [Fact]
    public void AbrasiveFilamentThroughASoftNozzleHolds()
    {
        IReadOnlyList<PrintCompatibilityFinding> findings =
            Evaluate(File(abrasive: true), Printer(), Tool(hardened: false));

        findings.Should().Contain(PrintCompatibilityFinding.AbrasiveFilamentNeedsHardenedNozzle);
        PrintFileCompatibility.WorstOf(findings).Should().Be(PrintCompatibilitySeverity.Hold);
    }

    [Fact]
    public void AbrasiveFilamentThroughAHardenedNozzleIsFine()
    {
        Evaluate(File(abrasive: true), Printer(), Tool(hardened: true)).Should().BeEmpty();
    }

    /// <summary>
    /// A CoreXY file arriving at a bed slinger - wear rather than a wasted print, so it holds.
    /// </summary>
    [Fact]
    public void AFileForAFasterMachineHolds()
    {
        IReadOnlyList<PrintCompatibilityFinding> findings =
            Evaluate(File(model: "COREONE"), Printer("1.3.5"), Tool());

        findings.Should().Contain(PrintCompatibilityFinding.IncompatiblePrinterModel);
        PrintFileCompatibility.WorstOf(findings).Should().Be(PrintCompatibilitySeverity.Hold);
    }

    /// <summary>And the same pair the other way, which is the direction firmware allows.</summary>
    [Fact]
    public void AFileForAnOlderMachineSaysNothing()
    {
        Evaluate(File(model: "MK4S"), Printer("7.1.0"), Tool()).Should().BeEmpty();
    }

    [Fact]
    public void AWrongNozzleDiameterWarnsRatherThanHolds()
    {
        IReadOnlyList<PrintCompatibilityFinding> findings =
            Evaluate(File(nozzle: 0.6f), Printer(), Tool(nozzle: 0.4f));

        findings.Should().Equal(PrintCompatibilityFinding.NozzleDiameterMismatch);
        PrintFileCompatibility.WorstOf(findings).Should().Be(PrintCompatibilitySeverity.Warn);
    }

    /// <summary>Firmware's own tolerance, so a file this accepts is one the printer accepts.</summary>
    [Fact]
    public void ADiameterInsideFirmwaresToleranceIsTheSameDiameter()
    {
        Evaluate(File(nozzle: 0.4f), Printer(), Tool(nozzle: 0.4001f)).Should().BeEmpty();
    }

    /// <summary>
    /// <b>Directional.</b> A high-flow file under-extrudes on a standard hotend; a standard file on
    /// a high-flow hotend merely leaves capacity unused.
    /// </summary>
    [Fact]
    public void HighFlowWarnsInOneDirectionOnly()
    {
        Evaluate(File(highFlow: true), Printer(), Tool(highFlow: false))
            .Should().Equal(PrintCompatibilityFinding.HighFlowNozzleRequired);

        Evaluate(File(highFlow: false), Printer(), Tool(highFlow: true)).Should().BeEmpty();
    }

    /// <summary>The findings come back worst-first, so a caller can act on the head of the list.</summary>
    [Fact]
    public void AFileCanBeWrongInSeveralWaysAtOnce()
    {
        IReadOnlyList<PrintCompatibilityFinding> findings =
            Evaluate(File(model: "COREONE", nozzle: 0.6f, abrasive: true, highFlow: true),
                     Printer("1.3.5"),
                     Tool(nozzle: 0.4f, hardened: false, highFlow: false));

        findings.Should().HaveCount(4);
        PrintFileCompatibility.WorstOf(findings).Should().Be(PrintCompatibilitySeverity.Hold);
    }

    /// <summary>
    /// <b>A missing half says nothing</b>, whichever half it is. Output from another slicer, a
    /// printer that has not sent INFO, a model nobody's table knows - all ordinary, none a finding.
    /// </summary>
    [Fact]
    public void AFileThatSaidNothingProducesNothing()
    {
        Evaluate(new HSFile { Name = "quiet.gcode", MetadataState = PrintFileMetadataState.Silent },
                 Printer(),
                 Tool(nozzle: 0.4f, hardened: false)).Should().BeEmpty();
    }

    [Fact]
    public void APrinterThatHasReportedNoToolsProducesNothing()
    {
        Evaluate(File(abrasive: true, highFlow: true), Printer(), []).Should().BeEmpty();
    }

    [Fact]
    public void AnUnknownModelOnEitherSideProducesNothing()
    {
        Evaluate(File(model: "MK2.5S"), Printer("1.4.0"), Tool()).Should().BeEmpty();
        Evaluate(File(model: "MK4"), Printer(null), Tool()).Should().BeEmpty();

        // A triple newer than the names table is a machine nobody here has heard of, which is the
        // deliberate silence rather than the accidental one below.
        Evaluate(File(model: "MK4"), Printer("9.9.9"), Tool()).Should().BeEmpty();
    }

    /// <summary>
    /// <b>The two sides speak different languages, and only one of them is a model name.</b>
    /// </summary>
    /// <remarks>
    /// The slicer writes <c>COREONE</c> into the file; the printer reports <c>1.3.5</c>. Comparing
    /// the second against the table of designations makes every real machine unrecognised, which this
    /// check reads as "say nothing" - silent about every real printer, while agreeing with any suite
    /// that feeds it names no printer sends. A designation still resolves, because losing that would
    /// trade one silent failure for another.
    /// </remarks>
    [Fact]
    public void APrinterIsRecognisedFromTheTripleItReportsAsWellAsFromAName()
    {
        Evaluate(File(model: "COREONE"), Printer("1.3.5"), Tool())
            .Should().Contain(PrintCompatibilityFinding.IncompatiblePrinterModel);

        Evaluate(File(model: "COREONE"), Printer("MK3.5"), Tool())
            .Should().Contain(PrintCompatibilityFinding.IncompatiblePrinterModel);
    }

    /// <summary>
    /// <b>A toolchanger with some tools hardened and some not warns rather than holding.</b> Which
    /// one the abrasive filament goes through is settled by the file's tool mapping, which firmware
    /// resolves at print time and this cannot see - so holding would stop legitimate prints on the
    /// machine where somebody most likely fitted the right nozzle to the right tool, and silence
    /// would say nothing about the one finding that costs hardware.
    /// </summary>
    [Fact]
    public void AToolchangerWithAMixtureOfNozzlesWarns()
    {
        IReadOnlyList<PrintCompatibilityFinding> findings = Evaluate(File(abrasive: true, nozzle: 0.4f),
                                                                     Printer(),
                                                                     MixedToolchanger());

        findings.Should().Equal(PrintCompatibilityFinding.AbrasiveFilamentMayUseASoftNozzle);
        PrintFileCompatibility.WorstOf(findings).Should().Be(PrintCompatibilitySeverity.Warn);
    }

    /// <summary>
    /// <b>The asymmetry is in the cost, not in what is known.</b> The same toolchanger tells this
    /// exactly as little about which nozzle diameter the print will use - and there it stays quiet,
    /// because a maybe about a bad print is noise where a maybe about permanent damage is not.
    /// </summary>
    [Fact]
    public void TheSameUncertaintyAboutDiameterOrFlowIsNotWorthSaying()
    {
        Evaluate(File(nozzle: 0.4f, highFlow: true), Printer(), MixedToolchanger()).Should().BeEmpty();
    }

    /// <summary>A toolchanger whose tools are all hardened is simply fine.</summary>
    [Fact]
    public void AToolchangerWithEveryNozzleHardenedSaysNothing()
    {
        IReadOnlyList<PrinterTool> tools =
        [
            new() { PrinterId = 1, ToolNumber = 1, Hardened = true, HighFlow = true, NozzleDiameter = 0.4f },
            new() { PrinterId = 1, ToolNumber = 2, Hardened = true, HighFlow = true, NozzleDiameter = 0.4f },
        ];

        Evaluate(File(abrasive: true, nozzle: 0.4f), Printer(), tools).Should().BeEmpty();
    }

    private static IReadOnlyList<PrinterTool> MixedToolchanger()
    {
        return
        [
            new PrinterTool { PrinterId = 1, ToolNumber = 1, Hardened = true, HighFlow = true, NozzleDiameter = 0.4f },
            new PrinterTool { PrinterId = 1, ToolNumber = 2, Hardened = false, HighFlow = false, NozzleDiameter = 0.6f },
        ];
    }

    /// <summary>
    /// With no tools reported the top-level diameter still answers, which is the whole story for a
    /// single-tool machine and the only figure an older report carries.
    /// </summary>
    [Fact]
    public void TheTopLevelDiameterIsUsedWhenNoToolWasReported()
    {
        Evaluate(File(nozzle: 0.6f), Printer(nozzle: 0.4f), [])
            .Should().Equal(PrintCompatibilityFinding.NozzleDiameterMismatch);
    }

    private static IReadOnlyList<PrintCompatibilityFinding> Evaluate(HSFile file,
                                                                     Printer printer,
                                                                     IReadOnlyList<PrinterTool> tools)
    {
        return PrintFileCompatibility.Evaluate(file, printer, tools);
    }

    private static HSFile File(string? model = null,
                               float? nozzle = null,
                               bool? abrasive = null,
                               bool? highFlow = null)
    {
        return new HSFile
        {
            Name = "model.bgcode",
            MetadataState = PrintFileMetadataState.Read,
            PrinterModel = model,
            NozzleDiameter = nozzle,
            RequiresHardenedNozzle = abrasive,
            RequiresHighFlowNozzle = highFlow,
        };
    }

    /// <summary>
    /// A printer as one actually arrives, which is the whole reason this helper takes a triple.
    /// </summary>
    /// <remarks>
    /// <b>No printer sends <c>MK4S</c>.</b> <c>INFO</c> carries <c>printer_type: "1.4.1"</c> and that
    /// string is what is stored, so a suite written in designations agrees with itself while the model
    /// rule makes no claim about any real machine - a green check that never fires. Written in
    /// triples, these fail if the resolution is removed.
    /// </remarks>
    private static Printer Printer(string? printerType = "1.4.1", float? nozzle = null)
    {
        return new Printer { Id = 1, Model = printerType, NozzleDiameter = nozzle };
    }

    private static IReadOnlyList<PrinterTool> Tool(float? nozzle = null,
                                                   bool hardened = true,
                                                   bool highFlow = true)
    {
        PrinterTool tool = new()
        {
            PrinterId = 1,
            ToolNumber = 1,
            NozzleDiameter = nozzle,
            Hardened = hardened,
            HighFlow = highFlow,
        };

        return [tool];
    }
}
