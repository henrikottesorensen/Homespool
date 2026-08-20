using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

using AwesomeAssertions;

using Homespool.Host.PrintFiles.GCode;

namespace Homespool.Host.Test;

/// <summary>
/// Reading what a print file says it was sliced for, in both containers a printer accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixtures are real slicer output</b>, not hand-assembled bytes, which is the whole point of
/// having them: a hand-written binary blob tests this parser against my reading of the
/// specification, and a sliced file tests it against what PrusaSlicer actually emits. Regenerate
/// them with a 3 mm cube and PrusaSlicer 2.9.6's CLI:
/// <code>
/// PrusaSlicer --export-gcode --printer-profile "Prusa CORE One HF0.4 nozzle" \
///   --print-profile "0.20mm SPEED @COREONE HF0.4" \
///   --material-profile "Esun PLA @COREONE HF0.4" -o metadata-coreone-hf04-pla.bgcode cube.stl
/// </code>
/// with <c>3D-Fuel Pro PCTG Matte Black @COREONE HF0.4</c> for the abrasive one, and the MK3.5
/// profile with and without <c>--binary-gcode=0</c> for the other two.
/// </para>
/// <para>
/// <b>The malformed cases are the ones with something to prove.</b> Every size in a binary file is
/// attacker-influenced, and this parser runs on whatever anybody uploads, so the tests that matter
/// are the ones where a declared length is a lie.
/// </para>
/// </remarks>
public class GCodeMetadataReaderTests
{
    /// <summary>
    /// A CORE One with a high-flow 0.4 nozzle, PLA. Binary, and the block is uncompressed - which is
    /// the finding the whole reader is built on.
    /// </summary>
    [Fact]
    public void ReadsARealBinaryFile()
    {
        GCodeMetadata? metadata = ReadFixture("metadata-coreone-hf04-pla.bgcode");

        metadata.Should().NotBeNull();
        metadata!.PrinterModel.Should().Be("COREONE");
        metadata.NozzleDiameters.Should().Equal(0.4f);
        metadata.FilamentTypes.Should().Equal("PLA");
        metadata.FilamentAbrasive.Should().Equal(false);
        metadata.NozzleHighFlow.Should().Equal(true);
    }

    /// <summary>
    /// The same printer and the same nozzle, sliced for a carbon-filled filament. The pair differ in
    /// one flag, which is the one that costs hardware rather than a print.
    /// </summary>
    [Fact]
    public void ReadsTheAbrasiveFlagFromARealFile()
    {
        GCodeMetadata? metadata = ReadFixture("metadata-coreone-hf04-abrasive.bgcode");

        metadata.Should().NotBeNull();
        metadata!.FilamentTypes.Should().Equal("PCTG");
        metadata.FilamentAbrasive.Should().Equal(true);
        metadata.AnyFilamentAbrasive.Should().BeTrue();
    }

    /// <summary>An MK3.5 slice exported as ASCII, where the same keys live in the trailing block.</summary>
    [Fact]
    public void ReadsARealAsciiFile()
    {
        GCodeMetadata? metadata = ReadFixture("metadata-mk35-04-pla.gcode");

        metadata.Should().NotBeNull();
        metadata!.PrinterModel.Should().Be("MK3.5");
        metadata.NozzleDiameters.Should().Equal(0.4f);
        metadata.FilamentTypes.Should().Equal("PLA");
        metadata.FilamentAbrasive.Should().Equal(false);
        metadata.NozzleHighFlow.Should().Equal(false);
    }

    /// <summary>
    /// <b>A file named <c>.gcode</c> is routinely binary.</b> PrusaSlicer honours the printer
    /// profile's <c>binary_gcode</c> setting and not the extension it was handed, so this is what
    /// that profile produces by default - and dispatching on the name would misread it.
    /// </summary>
    [Fact]
    public void ReadsABinaryFileNamedGCode()
    {
        GCodeMetadata? metadata = ReadFixture("metadata-mk35-binary-named-gcode.gcode");

        metadata.Should().NotBeNull();
        metadata!.PrinterModel.Should().Be("MK3.5");
        metadata.NozzleDiameters.Should().Equal(0.4f);
    }

    /// <summary>
    /// Output from a slicer that is not PrusaSlicer: readable, and it says nothing. That must not
    /// read as damage - it is most of the non-Prusa world.
    /// </summary>
    [Fact]
    public void AFileWithNoConfigurationBlockSaysNothing()
    {
        GCodeMetadata? metadata = Read("G28 ; home\nG1 X10 Y10 F3000\nM104 S200\n");

        metadata.Should().NotBeNull();
        metadata!.SaysNothing.Should().BeTrue();
    }

    [Fact]
    public void ReadsEveryExtruderInOrder()
    {
        GCodeMetadata? metadata = Read(Config("; nozzle_diameter = 0.4,0.6",
                                              "; filament_type = PLA;PETG",
                                              "; filament_abrasive = 0,1",
                                              "; nozzle_high_flow = 1,0",
                                              "; printer_model = XL"));

        metadata.Should().NotBeNull();
        metadata!.PrinterModel.Should().Be("XL");
        metadata.NozzleDiameters.Should().Equal(0.4f, 0.6f);
        metadata.FilamentTypes.Should().Equal("PLA", "PETG");
        metadata.FilamentAbrasive.Should().Equal(false, true);
        metadata.NozzleHighFlow.Should().Equal(true, false);
    }

    /// <summary>
    /// <b>Any, not each.</b> One abrasive spool among several wears the single nozzle they all pass
    /// through, which is the question a non-toolchanger asks.
    /// </summary>
    [Fact]
    public void OneAbrasiveFilamentMakesThePrintAbrasive()
    {
        Read(Config("; filament_abrasive = 0,0,1,0,0"))!.AnyFilamentAbrasive.Should().BeTrue();
        Read(Config("; filament_abrasive = 0,0"))!.AnyFilamentAbrasive.Should().BeFalse();
        Read(Config("; printer_model = MK4S"))!.AnyFilamentAbrasive.Should().BeNull();
    }

    /// <summary>A user-named filament can contain the separator, so the slicer quotes it.</summary>
    [Fact]
    public void ReadsAQuotedFilamentName()
    {
        Read(Config("; filament_type = \"PLA; the good one\";PETG"))!
            .FilamentTypes.Should().Equal("PLA; the good one", "PETG");
    }

    /// <summary>
    /// <b>Dropped whole, never in part.</b> The lists are positional, so salvaging a list by
    /// skipping the element that would not parse would silently renumber every extruder after it.
    /// </summary>
    [Fact]
    public void AListWithOneUnreadableEntryIsDiscardedEntirely()
    {
        GCodeMetadata? metadata = Read(Config("; nozzle_diameter = 0.4,fnord,0.6",
                                              "; printer_model = MK4S"));

        metadata.Should().NotBeNull();
        metadata!.NozzleDiameters.Should().BeEmpty();
        metadata.PrinterModel.Should().Be("MK4S", "one unreadable key does not discard the others");
    }

    /// <summary>
    /// The slicer writes a decimal point wherever it runs. Parsing under a comma-decimal culture
    /// must not turn 0.4 into 4 (<c>notes/floating-point.md</c>).
    /// </summary>
    [Fact]
    public void TheDecimalSeparatorIsNotTheMachinesBusiness()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");

            Read(Config("; nozzle_diameter = 0.4"))!.NozzleDiameters.Should().Equal(0.4f);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>A value containing an <c>=</c> splits on the first one, not the last.</summary>
    [Fact]
    public void SplitsOnTheFirstSeparator()
    {
        Read(Config("; printer_model = MK4S", "; objects_info = {\"a\":\"b=c\"}"))!
            .PrinterModel.Should().Be("MK4S");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("GC", "shorter than the magic")]
    [InlineData("GCDE", "the magic and nothing else")]
    public void RefusesWhatItCannotRead(string content, string reason)
    {
        Read(content).Should().BeNull(reason);
    }

    /// <summary>
    /// A version this does not know is refused rather than read optimistically: an unreadable file
    /// makes no claims, where a misread one makes wrong ones.
    /// </summary>
    [Fact]
    public void RefusesAnUnknownBinaryVersion()
    {
        byte[] file = BinaryHeader(version: 2, checksumType: 1);

        Read(file).Should().BeNull();
    }

    /// <summary>
    /// The checksum algorithm decides how many bytes sit between one block and the next, so an
    /// unknown one is not a skippable curiosity - every later offset would be wrong.
    /// </summary>
    [Fact]
    public void RefusesAnUnknownChecksumType()
    {
        byte[] file = BinaryHeader(version: 1, checksumType: 7);

        Read(file).Should().BeNull();
    }

    /// <summary>
    /// A block header promising more bytes than the file holds - the shape an interrupted upload
    /// takes, and the shape a hostile file takes.
    /// </summary>
    [Fact]
    public void RefusesABlockLongerThanTheFile()
    {
        byte[] header = BinaryHeader(version: 1, checksumType: 0);
        byte[] file =
        [
            .. header,

            // A printer metadata block declaring 4 GB of uncompressed INI, with nothing behind it.
            0x03, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00,
        ];

        Read(file).Should().BeNull();
    }

    /// <summary>Truncated part-way through a real file, which is what a failed upload leaves.</summary>
    [Fact]
    public void RefusesATruncatedRealFile()
    {
        byte[] whole = File.ReadAllBytes(FixturePath("metadata-coreone-hf04-pla.bgcode"));

        Read(whole[..40]).Should().BeNull();
    }

    private static string Config(params string[] lines)
    {
        StringBuilder file = new();

        file.Append("G28 ; home\nG1 X10 Y10 F3000\n\n; prusaslicer_config = begin\n");

        foreach (string line in lines)
        {
            file.Append(line).Append('\n');
        }

        file.Append("; prusaslicer_config = end\n");

        return file.ToString();
    }

    private static GCodeMetadata? Read(string content)
    {
        return Read(Encoding.UTF8.GetBytes(content));
    }

    private static GCodeMetadata? Read(byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);

        return GCodeMetadataReader.Read(stream);
    }

    private static GCodeMetadata? ReadFixture(string name)
    {
        return GCodeMetadataReader.ReadFile(FixturePath(name));
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, name);
    }

    /// <summary>The ten-byte file header: magic, version, checksum type. Little-endian throughout.</summary>
    private static byte[] BinaryHeader(uint version, ushort checksumType)
    {
        byte[] header = new byte[10];

        "GCDE"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), checksumType);

        return header;
    }
}
