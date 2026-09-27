using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.Pages.Printers;
using Homespool.Host.PrintFiles.GCode;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// <see cref="PlateDrawing"/> - the printer's count of objects, with the slicer's description laid
/// over it where the two agree.
/// </summary>
public class PlateDrawingTests
{
    private static readonly IReadOnlySet<int> NoneCancelled = new HashSet<int>();

    private static PlateObject Square(int id, string name, double x, double y)
    {
        return new PlateObject(id, name,
        [
            new PlatePoint(x + 5, y + 5), new PlatePoint(x - 5, y + 5),
            new PlatePoint(x - 5, y - 5), new PlatePoint(x + 5, y - 5),
        ]);
    }

    private static readonly PlateLayout TwoSquares = new(
        [Square(0, "front", 50, 20), Square(1, "back", 50, 190)],
        new PlateBounds(0, 0, 250, 210));

    /// <summary>
    /// The list is the printer's count, numbered from one, with the ids underneath unchanged - the
    /// number is what a person reads and the id is what a form posts.
    /// </summary>
    [Fact]
    public void ObjectsAreNumberedFromOneOverTheirIds()
    {
        PlateDrawing drawing = PlateDrawing.For(2, NoneCancelled, TwoSquares);

        drawing.Objects.Select(item => (item.Id, item.Number, item.Name)).Should().Equal((0, 1, "front"), (1, 2, "back"));
    }

    /// <summary>
    /// <b>A header that disagrees with the printer about how many objects there are is not used.</b>
    /// The join is positional, so a count one short would put every later name on its neighbour's
    /// button. The objects are still listed, by number, from the printer's own count.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AHeaderThatDisagreesWithThePrinterIsNotUsed(int printerCount)
    {
        PlateDrawing drawing = PlateDrawing.For(printerCount, NoneCancelled, TwoSquares);

        drawing.Objects.Should().HaveCount(printerCount);
        drawing.Objects.Should().OnlyContain(item => item.Name == null && item.Outline.Count == 0);
        drawing.Frame.Should().BeNull("with nothing to draw there is no drawing, only the list");
    }

    /// <summary>The printer's cancelled set decides which objects are cancelled, by id.</summary>
    [Fact]
    public void CancelledIsThePrintersWord()
    {
        PlateDrawing drawing = PlateDrawing.For(2, new HashSet<int> { 1 }, TwoSquares);

        drawing.Objects.Select(item => item.Cancelled).Should().Equal(false, true);
        drawing.CancelledCount.Should().Be(1);
    }

    /// <summary>
    /// <b>The back of the bed is drawn at the top</b>, as it looks from the front of the machine:
    /// the printer's y grows away from the viewer and SVG's grows down the page.
    /// </summary>
    [Fact]
    public void TheBackOfTheBedIsDrawnAtTheTop()
    {
        PlateDrawing drawing = PlateDrawing.For(2, NoneCancelled, TwoSquares);

        drawing.ViewBox.Should().Be("0 0 250 210");

        (string _, string frontY) = drawing.LabelFor(drawing.Objects[0]);
        (string _, string backY) = drawing.LabelFor(drawing.Objects[1]);

        frontY.Should().Be("190", "y = 20 on the bed is 20 mm from the front, near the bottom of the drawing");
        backY.Should().Be("20");
        drawing.PathFor(drawing.Objects[0]).Should().Be("M55 185 L45 185 L45 195 L55 195 Z");
    }

    /// <summary>
    /// Without the file's bed the drawing fits itself around the objects with a margin, and says so,
    /// so it is not drawn as though it were the bed.
    /// </summary>
    [Fact]
    public void WithoutABedTheFrameFitsTheObjects()
    {
        PlateDrawing drawing = PlateDrawing.For(2, NoneCancelled, TwoSquares with { Bed = null });

        drawing.BedStated.Should().BeFalse();
        drawing.Frame.Should().NotBeNull();

        // The squares span x 45..55 and y 15..195; the margin is the larger of 5 mm and 8% of 180.
        PlateBounds frame = drawing.Frame!;
        frame.MinX.Should().BeApproximately(45 - 14.4, 1e-9);
        frame.MinY.Should().BeApproximately(15 - 14.4, 1e-9);
        frame.MaxX.Should().BeApproximately(55 + 14.4, 1e-9);
        frame.MaxY.Should().BeApproximately(195 + 14.4, 1e-9);
    }

    /// <summary>Coordinates are written with a full stop whatever the culture, since SVG reads nothing else.</summary>
    [Fact]
    public void CoordinatesAreInvariant()
    {
        using CultureScope culture = new("da-DK");

        PlateDrawing drawing = PlateDrawing.For(1, NoneCancelled,
                                                new PlateLayout([Square(0, "a", 10.25, 10.25)], new PlateBounds(0, 0, 250, 210)));

        drawing.PathFor(drawing.Objects[0]).Should().Contain("15.25").And.NotContain(",");
    }

    /// <summary>Sets the thread's culture for one test and puts it back.</summary>
    private sealed class CultureScope : System.IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name)
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
        }

        public void Dispose()
        {
            System.Globalization.CultureInfo.CurrentCulture = _previous;
        }
    }
}
