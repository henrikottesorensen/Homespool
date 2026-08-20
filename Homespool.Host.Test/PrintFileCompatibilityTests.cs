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
            Evaluate(File(model: "COREONE"), Printer(model: "MK3.5"), Tool());

        findings.Should().Contain(PrintCompatibilityFinding.IncompatiblePrinterModel);
        PrintFileCompatibility.WorstOf(findings).Should().Be(PrintCompatibilitySeverity.Hold);
    }

    /// <summary>And the same pair the other way, which is the direction firmware allows.</summary>
    [Fact]
    public void AFileForAnOlderMachineSaysNothing()
    {
        Evaluate(File(model: "MK4S"), Printer(model: "COREONE"), Tool()).Should().BeEmpty();
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
                     Printer(model: "MK3.5"),
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
        Evaluate(new PrintFile { Name = "quiet.gcode", MetadataState = PrintFileMetadataState.Silent },
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
        Evaluate(File(model: "MK2.5S"), Printer(model: "MK4"), Tool()).Should().BeEmpty();
        Evaluate(File(model: "MK4"), Printer(model: null), Tool()).Should().BeEmpty();
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

    private static IReadOnlyList<PrintCompatibilityFinding> Evaluate(PrintFile file,
                                                                     Printer printer,
                                                                     IReadOnlyList<PrinterTool> tools)
    {
        return PrintFileCompatibility.Evaluate(file, printer, tools);
    }

    private static PrintFile File(string? model = null,
                                  float? nozzle = null,
                                  bool? abrasive = null,
                                  bool? highFlow = null)
    {
        return new PrintFile
        {
            Name = "model.bgcode",
            MetadataState = PrintFileMetadataState.Read,
            PrinterModel = model,
            NozzleDiameter = nozzle,
            RequiresHardenedNozzle = abrasive,
            RequiresHighFlowNozzle = highFlow,
        };
    }

    private static Printer Printer(string? model = "MK4S", float? nozzle = null)
    {
        return new Printer { Id = 1, Model = model, NozzleDiameter = nozzle };
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
