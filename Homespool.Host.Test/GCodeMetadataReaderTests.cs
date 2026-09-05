using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
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
/// <b>The hostile container shapes - lying sizes, decompression bombs, truncation - are
/// libbgcode.NET's tests now</b>, alongside the walking and decompression they attack. What stays
/// here is the seam: dispatch between the two containers, the three-outcome contract, the
/// interpretation of what the blocks say, and a mutation sweep pinning that nothing thrown in the
/// library escapes the adapter.
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
    /// must not turn 0.4 into 4.
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
    /// The megabyte bound on a metadata payload is this adapter's policy, not the library's - the
    /// library would happily read far more - so the refusal is pinned here: a block declaring more
    /// than the bound makes the file unreadable, never silently empty.
    /// </summary>
    [Fact]
    public void RefusesAMetadataBlockPastTheMegabyteBound()
    {
        byte[] payload = new byte[2 * 1024 * 1024];
        byte[] file = new byte[10 + 8 + 2 + payload.Length];

        "GCDE"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10, 2), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14, 4), (uint)payload.Length);

        Read(file).Should().BeNull();
    }

    /// <summary>Truncated part-way through a real file, which is what a failed upload leaves.</summary>
    [Fact]
    public void RefusesATruncatedRealFile()
    {
        byte[] whole = File.ReadAllBytes(FixturePath("metadata-coreone-hf04-pla.bgcode"));

        Read(whole[..40]).Should().BeNull();
    }

    /// <summary>
    /// The exception contract under mutation: whatever the bytes, <c>Read</c> answers with
    /// metadata or null and never throws. Distilled from a fuzzing pass that found a seek throwing
    /// on an array-backed stream; the seed is fixed so a failure reproduces exactly.
    /// </summary>
    [Fact]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
                     Justification = "The randomness generates hostile test inputs; a fixed seed making failures reproducible is the point.")]
    public void NoMutationOfARealFileEscapesTheContract()
    {
        Random rng = new(20260830);
        byte[][] seeds =
        [
            File.ReadAllBytes(FixturePath("metadata-coreone-hf04-pla.bgcode")),
            File.ReadAllBytes(FixturePath("metadata-mk35-04-pla.gcode")),
            File.ReadAllBytes(FixturePath("metadata-mk35-binary-named-gcode.gcode")),
        ];
        uint[] interestingSizes = [0, 1, (1024 * 1024) - 1, 1024 * 1024, (1024 * 1024) + 1, int.MaxValue, uint.MaxValue];

        foreach (byte[] seed in seeds)
        {
            for (int variant = 0; variant < 500; variant++)
            {
                byte[] mutated = (byte[])seed.Clone();

                for (int edits = 1 + rng.Next(16); edits > 0 && mutated.Length > 0; edits--)
                {
                    switch (rng.Next(4))
                    {
                        case 0:
                            mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);
                            break;

                        case 1:
                            mutated[rng.Next(mutated.Length)] ^= (byte)(1 << rng.Next(8));
                            break;

                        case 2 when mutated.Length >= 4:
                            BinaryPrimitives.WriteUInt32LittleEndian(
                                mutated.AsSpan(rng.Next(mutated.Length - 3), 4),
                                interestingSizes[rng.Next(interestingSizes.Length)]);
                            break;

                        default:
                            mutated = mutated[..rng.Next(mutated.Length + 1)];
                            break;
                    }
                }

                // The assertion is that this line returns at all: any escape fails the test.
                Read(mutated);
            }
        }
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
}
