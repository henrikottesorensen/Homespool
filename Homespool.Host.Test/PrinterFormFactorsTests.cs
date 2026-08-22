using AwesomeAssertions;

using Homespool.Host.Pages;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Which drawing the front page gives a printer.
/// </summary>
/// <remarks>
/// <b>These pin a deliberate refusal as much as a behaviour.</b> The obvious implementation is a
/// table keyed on the model group, and every row of one would be a hardware claim nothing here can
/// check. Everything below therefore drives off what the printer reported rather than what it is
/// called, and <see cref="DrawsTwoPrintersOfOneModelDifferently"/> exists to fail if somebody
/// reintroduces the lookup.
/// </remarks>
public class PrinterFormFactorsTests
{
    /// <summary>
    /// A printer that has never reported gets the open frame rather than nothing. The front page
    /// always draws something, and open is the commoner machine.
    /// </summary>
    [Fact]
    public void DrawsAnOpenFrameWhenNothingHasBeenReported()
    {
        PrinterFormFactors.For(null).Should().Be(PrinterFormFactor.Open);
    }

    /// <summary>
    /// Connected, reporting, and with neither a chamber nor an enclosure reading: an open frame. This
    /// is the ordinary case and the one that must not drift to <c>Enclosed</c> on some other field.
    /// </summary>
    [Fact]
    public void DrawsAnOpenFrameWhenNeitherChamberNorEnclosureIsReported()
    {
        PrinterLiveState live = new() { NozzleTemperature = 215, BedTemperature = 60 };

        PrinterFormFactors.For(live).Should().Be(PrinterFormFactor.Open);
    }

    /// <summary>
    /// A chamber temperature means a sensor sitting in a chamber, which is the closest thing to proof
    /// the wire carries.
    /// </summary>
    [Fact]
    public void DrawsABoxWhenAChamberIsReported()
    {
        PrinterLiveState live = new() { ChamberTemperature = 31.5f };

        PrinterFormFactors.For(live).Should().Be(PrinterFormFactor.Enclosed);
    }

    /// <summary>
    /// An enclosure reading counts on its own. The two are separate fields on the wire and a printer
    /// may report either, so neither may be treated as implying the other.
    /// </summary>
    [Fact]
    public void DrawsABoxWhenAnEnclosureIsReported()
    {
        PrinterLiveState live = new() { EnclosureTemperature = 28 };

        PrinterFormFactors.For(live).Should().Be(PrinterFormFactor.Enclosed);
    }

    /// <summary>
    /// <b>The whole reason this is not a model lookup.</b> The drawing follows what the printer
    /// reported, so two printers could report the same model and still differ. A table keyed on the
    /// model group cannot express that at all - it would answer identically for both, whichever of
    /// them was right.
    /// </summary>
    [Fact]
    public void DrawsTwoPrintersOfOneModelDifferently()
    {
        PrinterLiveState bare = new() { NozzleTemperature = 200 };
        PrinterLiveState fitted = new() { NozzleTemperature = 200, EnclosureTemperature = 30 };

        PrinterFormFactors.For(bare).Should().Be(PrinterFormFactor.Open);
        PrinterFormFactors.For(fitted).Should().Be(PrinterFormFactor.Enclosed);
    }

    /// <summary>
    /// A chamber reading of zero is still a reading. <c>HasValue</c> is the test rather than truthiness
    /// - a cold chamber is a chamber.
    /// </summary>
    [Fact]
    public void TreatsAColdChamberAsAChamber()
    {
        PrinterLiveState live = new() { ChamberTemperature = 0 };

        PrinterFormFactors.For(live).Should().Be(PrinterFormFactor.Enclosed);
    }
}
