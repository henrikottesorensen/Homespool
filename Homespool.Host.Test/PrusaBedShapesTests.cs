using AwesomeAssertions;

using Homespool.Host.PrintFiles.GCode;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="PrusaBedShapes"/> - each Prusa model's bed, copied from PrusaSlicer's own profiles.
/// </summary>
public class PrusaBedShapesTests
{
    /// <summary>
    /// <b>Every entry names the model the firmware's triple names.</b> The table is keyed on the
    /// triple and carries the name only to be checked, so a triple that ever came to mean another
    /// model fails here rather than drawing somebody's print on the wrong bed.
    /// </summary>
    [Fact]
    public void EveryTripleNamesTheModelItsEntrySays()
    {
        foreach ((string printerType, string name, string _) in PrusaBedShapes.Entries)
        {
            PrinterModelNames.ForPrinterType(printerType).Should().Be(name, $"{printerType} is the firmware's {name}");
        }
    }

    /// <summary>Every entry is a bed the plate reader accepts, so none silently draws nothing.</summary>
    [Fact]
    public void EveryEntryIsABed()
    {
        foreach ((string printerType, string _, string _) in PrusaBedShapes.Entries)
        {
            PrusaBedShapes.For(printerType).Should().NotBeNull(printerType);
        }
    }

    /// <summary>The printer the plate view was first seen on, a Core One+, and the MK3.5 of the six-cube captures.</summary>
    [Theory]
    [InlineData("7.1.0", 250, 220)]
    [InlineData("1.3.5", 250, 210)]
    public void AModelsBedIsItsSize(string printerType, double width, double depth)
    {
        PrusaBedShapes.For(printerType).Should().Be(new PlateBounds(0, 0, width, depth));
    }

    /// <summary>
    /// A model with no preset at the profile's tag - the iX here - or none reported has no bed, and
    /// its plate is framed around the objects instead.
    /// </summary>
    [Theory]
    [InlineData("4.1.0")]
    [InlineData("9.9.9")]
    [InlineData(null)]
    public void AModelWithNoEntryHasNoBed(string? printerType)
    {
        PrusaBedShapes.For(printerType).Should().BeNull();
    }
}
