using AwesomeAssertions;

using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// Which printers may print which files, mirrored from firmware's own compatibility table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The direction is the point, so nearly every case here is asserted both ways round.</b> The
/// relation is an upgrade path, not a similarity: a newer machine prints an older machine's files
/// and never the reverse, because the reverse is a bed slinger being asked to sustain a CoreXY's
/// accelerations.
/// </para>
/// <para>
/// <b>Blocking makes a false positive expensive</b>, which is what the unknown-model cases are for:
/// a model this table has never heard of must yield no claim at all rather than a refusal, or every
/// MK2-era file and every printer newer than the table stops working.
/// </para>
/// </remarks>
public class PrinterModelCompatibilityTests
{
    /// <summary>The case this whole check exists for: CoreXY speeds arriving at a bed slinger.</summary>
    [Fact]
    public void ACoreOneFileIsRefusedByABedSlinger()
    {
        PrinterModelCompatibility.CanPrint("MK3.5", "COREONE").Should().BeFalse();
    }

    /// <summary>And the same pair the other way round, which is the safe direction.</summary>
    [Fact]
    public void ACoreOneAcceptsTheOlderMachinesFiles()
    {
        PrinterModelCompatibility.CanPrint("COREONE", "MK4S").Should().BeTrue();
        PrinterModelCompatibility.CanPrint("COREONE", "MK4").Should().BeTrue();
        PrinterModelCompatibility.CanPrint("COREONE", "MK3").Should().BeTrue();
    }

    [Theory]
    [InlineData("MK3.5", "MK3", "the upgrade kit's whole promise")]
    [InlineData("MK3.5", "MK3S", "same group as MK3")]
    [InlineData("MK4", "MK3", "MK4 takes MK3 gcode in compatibility mode")]
    [InlineData("MK4S", "MK4", "one step up the chain")]
    [InlineData("MK4S", "MK3", "two steps, reached by recursion")]
    [InlineData("COREONEL", "COREONE", "physically larger, otherwise the same")]
    [InlineData("XLP", "XL", "the XL+ takes XL gcode, added in firmware 6.8")]
    [InlineData("MK3.9", "MK4", "MK3.9 and MK4 are one group")]
    public void APrinterAcceptsWhatIsBelowItInTheChain(string printer, string file, string because)
    {
        PrinterModelCompatibility.CanPrint(printer, file).Should().BeTrue(because);
    }

    [Theory]
    [InlineData("MK3", "MK3.5", "an MK3 is not an MK3.5")]
    [InlineData("MK4", "MK4S", "the chain runs the other way")]
    [InlineData("MK4S", "COREONE", "a CoreXY file on a bed slinger")]
    [InlineData("COREONE", "COREONEL", "the larger machine's files do not fit")]
    [InlineData("MINI", "MK3", "MINI has no backwards compatibility at all")]
    [InlineData("XL", "MK4", "nor does the XL")]
    [InlineData("iX", "MK4", "nor the iX")]
    [InlineData("XL", "XLP", "the XL+ is the newer machine, so its files do not come back")]
    public void APrinterRefusesWhatIsAboveItOrBesideIt(string printer, string file, string because)
    {
        PrinterModelCompatibility.CanPrint(printer, file).Should().BeFalse(because);
    }

    /// <summary>
    /// <b>The slicer and firmware do not share a vocabulary.</b> <c>MK4IS</c> is an MK4 with Input
    /// Shaping switched on - the same hardware - and firmware's table has no such entry, so the
    /// suffix is stripped before the lookup.
    /// </summary>
    [Theory]
    [InlineData("MK4IS")]
    [InlineData("MK4ISMMU3")]
    [InlineData("MK4MMU3")]
    public void TheSlicersSuffixesNameFirmwareAndAddOnsRatherThanMachines(string fileModel)
    {
        PrinterModelCompatibility.CanPrint("MK4", fileModel).Should().BeTrue();
    }

    /// <summary>
    /// <b>The slicer names a machine by its toolhead count and firmware does not.</b> Every one of
    /// these is firmware's single <c>XL</c>, and before they were mapped the only XL getting a model
    /// check at all was a one-toolhead one.
    /// </summary>
    [Theory]
    [InlineData("XL")]
    [InlineData("XLIS")]
    [InlineData("XL2")]
    [InlineData("XL2IS")]
    [InlineData("XL5")]
    [InlineData("XL5IS")]
    public void EveryToolheadCountOfTheXlIsTheSameMachine(string fileModel)
    {
        PrinterModelCompatibility.GroupFor(fileModel).Should().Be(PrinterModelGroup.Xl);
        PrinterModelCompatibility.CanPrint("XL", fileModel).Should().BeTrue();
    }

    /// <summary>The INDX counts its tools the same way, against firmware's one <c>COREONEINDX</c>.</summary>
    [Theory]
    [InlineData("COREONE_INDX4T")]
    [InlineData("COREONE_INDX8T")]
    [InlineData("COREONEINDX")]
    public void TheIndxCountsItsToolsToo(string fileModel)
    {
        PrinterModelCompatibility.GroupFor(fileModel).Should().Be(PrinterModelGroup.CoreOneIndx);
        PrinterModelCompatibility.CanPrint("COREONEINDX", fileModel).Should().BeTrue();
    }

    /// <summary>
    /// The toolhead rule must not reach the digits that are part of a model's name - an MK2.5
    /// turning into an MK2 would be a machine that predates all of this.
    /// </summary>
    [Theory]
    [InlineData("MK2.5")]
    [InlineData("MK2S")]
    public void TheToolheadRuleDoesNotEatAModelsOwnDigits(string fileModel)
    {
        PrinterModelCompatibility.GroupFor(fileModel).Should().Be(PrinterModelGroup.Unknown);
    }

    /// <summary>An INDX file is still not something an ordinary CORE One should be sent.</summary>
    [Fact]
    public void AnIndxFileDoesNotGoToAPlainCoreOne()
    {
        PrinterModelCompatibility.CanPrint("COREONE", "COREONE_INDX8T").Should().BeFalse();
    }

    [Fact]
    public void MiniIsIsStillAMini()
    {
        PrinterModelCompatibility.CanPrint("MINI", "MINIIS").Should().BeTrue();
        PrinterModelCompatibility.CanPrint("MINI", "MINI").Should().BeTrue();
    }

    /// <summary>
    /// A model on either side that this table does not know makes <b>no claim</b>. Firmware refuses
    /// in the same situation; this runs before the bytes are sent, where refusing would block every
    /// MK2-era file and every printer newer than the table.
    /// </summary>
    [Theory]
    [InlineData("MK4", "MK2.5S", "the slicer still ships MK2-era profiles firmware never listed")]
    [InlineData("MK4", "MK2SMM", "nor the MK2 with its multi-material upgrade")]
    [InlineData("PRUSA9000", "MK4", "a printer newer than this table")]
    [InlineData(null, "MK4", "a printer that has not sent INFO yet")]
    [InlineData("MK4", null, "a file that did not say")]
    [InlineData("MK4", "", "or said nothing")]
    public void AnUnknownModelOnEitherSideMakesNoClaim(string? printer, string? file, string because)
    {
        PrinterModelCompatibility.CanPrint(printer, file).Should().BeNull(because);
    }

    /// <summary>
    /// The printer reports <c>iX</c> and a vendor bundle may write it otherwise, so neither side's
    /// casing decides anything.
    /// </summary>
    [Fact]
    public void CasingIsNotTheAnswer()
    {
        PrinterModelCompatibility.CanPrint("ix", "IX").Should().BeTrue();
        PrinterModelCompatibility.CanPrint("coreone", "mk4s").Should().BeTrue();
    }

    /// <summary>
    /// Every model designation PrusaSlicer 2.9.6's vendor bundle ships, and what this table makes of
    /// it - the coverage claim itself, rather than a sample of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The list is the full output of</b> <c>grep '^printer_model = ' PrusaResearch.ini</c>, 37
    /// entries. Written out because the interesting failures are the ones nobody would think to
    /// sample: <c>XL2</c> and <c>XL5</c> both fell through as unknown until this was run, so an XL
    /// with more than one toolhead had no model check at all.
    /// </para>
    /// <para>
    /// <b>The six unmapped ones are unmapped on purpose.</b> They are MK2-era machines, which
    /// firmware's table has never listed because they run neither this firmware nor this protocol -
    /// they cannot reach Homespool at all, so a file sliced for one has no printer here to be
    /// compared against.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("COREONE")]
    [InlineData("COREONE_INDX4T")]
    [InlineData("COREONE_INDX8T")]
    [InlineData("COREONEL")]
    [InlineData("COREONELMMU3")]
    [InlineData("COREONEMMU3")]
    [InlineData("COREONEOAK")]
    [InlineData("MINI")]
    [InlineData("MINIIS")]
    [InlineData("MK3")]
    [InlineData("MK3.5")]
    [InlineData("MK3.5MMU3")]
    [InlineData("MK3.9")]
    [InlineData("MK3.9MMU3")]
    [InlineData("MK3.9S")]
    [InlineData("MK3.9SMMU3")]
    [InlineData("MK3MMU2")]
    [InlineData("MK3S")]
    [InlineData("MK3SMMU2S")]
    [InlineData("MK3SMMU3")]
    [InlineData("MK4")]
    [InlineData("MK4IS")]
    [InlineData("MK4ISMMU3")]
    [InlineData("MK4S")]
    [InlineData("MK4SMMU3")]
    [InlineData("XL")]
    [InlineData("XL2")]
    [InlineData("XL2IS")]
    [InlineData("XL5")]
    [InlineData("XL5IS")]
    [InlineData("XLIS")]
    public void EveryModelTheSlicerShipsIsRecognised(string slicerModel)
    {
        PrinterModelCompatibility.GroupFor(slicerModel).Should().NotBe(PrinterModelGroup.Unknown);
    }

    [Theory]
    [InlineData("MK2.5")]
    [InlineData("MK2.5MMU2")]
    [InlineData("MK2.5S")]
    [InlineData("MK2.5SMMU2S")]
    [InlineData("MK2S")]
    [InlineData("MK2SMM")]
    public void TheMk2EraIsUnmappedOnPurpose(string slicerModel)
    {
        PrinterModelCompatibility.GroupFor(slicerModel).Should().Be(PrinterModelGroup.Unknown);
    }

    /// <summary>Firmware groups the Oak finish with the plain CORE One - a finish, not a machine.</summary>
    [Fact]
    public void TheOakIsACoreOne()
    {
        PrinterModelCompatibility.GroupFor("COREONEOAK").Should().Be(PrinterModelGroup.CoreOne);
        PrinterModelCompatibility.CanPrint("COREONE", "COREONEOAK").Should().BeTrue();
    }
}
