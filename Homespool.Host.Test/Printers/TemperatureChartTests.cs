using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.Pages.Printers;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Test.Printers;

/// <summary>
/// The temperature graph's geometry - the part of the drawing that can be wrong without looking
/// wrong.
/// </summary>
public sealed class TemperatureChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The two path commands, so a coordinate can be read back out of the drawing.</summary>
    private static readonly char[] Commands = ['M', 'L'];

    /// <summary>Reads the points back out of a path, as (x, y) pairs.</summary>
    private static (double x, double y)[] PointsOf(string path)
    {
        return path.Split(Commands, StringSplitOptions.RemoveEmptyEntries)
                   .Select(segment => segment.Trim().Split(' '))
                   .Select(pair => (double.Parse(pair[0], CultureInfo.InvariantCulture),
                                    double.Parse(pair[1], CultureInfo.InvariantCulture)))
                   .ToArray();
    }

    private static TemperatureSeries SeriesOf(params (int minute, double? nozzle, double? bed)[] points)
    {
        List<TemperaturePoint> mapped = points
            .Select(p => new TemperaturePoint(Start.AddMinutes(p.minute), p.nozzle, p.bed, null, null))
            .ToList();

        return new TemperatureSeries(Start, Start.AddMinutes(60), mapped);
    }

    /// <summary>
    /// Nothing to draw is answered with nothing, rather than with axes around an empty box.
    /// </summary>
    [Fact]
    public void NoPointsDrawsNothing()
    {
        TemperatureChart.For(TemperatureSeries.Empty(Start, Start.AddHours(1))).Should().BeNull();
    }

    /// <summary>
    /// Points that carry no reading are the same case: a printer that was connected and silent has
    /// no temperature to plot, and a flat line at zero would be a claim about a machine nobody heard
    /// from.
    /// </summary>
    [Fact]
    public void PointsWithNoReadingsDrawNothing()
    {
        TemperatureChart.For(SeriesOf((0, null, null), (10, null, null))).Should().BeNull();
    }

    /// <summary>
    /// A gap lifts the pen. Without this the line is drawn straight across a stretch the printer
    /// said nothing about, inventing a temperature it never reported.
    /// </summary>
    [Fact]
    public void AGapStartsAFreshSubpath()
    {
        TemperatureChart chart = TemperatureChart.For(SeriesOf(
            (0, 200, 60),
            (10, 205, 60),
            (20, null, 60),
            (30, 210, 60),
            (40, 215, 60)))!;

        // Two runs of two readings each, so two moves and two lines - not one move and four lines,
        // which is what drawing straight across the gap would produce.
        chart.NozzlePath.Count(character => character == 'M').Should().Be(2);
        chart.NozzlePath.Count(character => character == 'L').Should().Be(2);

        // The bed reported throughout, so it is one unbroken run.
        chart.BedPath.Count(character => character == 'M').Should().Be(1);
    }

    /// <summary>
    /// Coordinates are invariant whatever the reader's culture, because SVG parses only a full stop.
    /// </summary>
    /// <remarks>
    /// <b>This is the failure that would look like a broken feature rather than a formatting slip.</b>
    /// A Danish reader formatting 123.45 as <c>123,45</c> puts a comma into a path, where a comma is
    /// a coordinate separator - so the graph does not draw wrongly, it silently draws nothing.
    /// </remarks>
    [Fact]
    public void PathCoordinatesAreInvariantOfCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("da-DK");

            TemperatureChart chart = TemperatureChart.For(SeriesOf((0, 213.7, 61.4), (30, 214.9, 61.6)))!;

            chart.NozzlePath.Should().NotContain(",");
            chart.NozzlePath.Should().MatchRegex(@"^M[\d.]+ [\d.]+ L[\d.]+ [\d.]+$");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The axis clears the hottest reading, so a trace never runs along the top edge where it cannot
    /// be told from a clipped one.
    /// </summary>
    [Fact]
    public void TheAxisClearsTheHottestReading()
    {
        TemperatureChart chart = TemperatureChart.For(SeriesOf((0, 250, 100)))!;

        chart.Ceiling.Should().BeGreaterThan(250);
    }

    /// <summary>
    /// A cold printer still gets a scale somebody recognises rather than one stretched over the few
    /// degrees a room drifts by.
    /// </summary>
    [Fact]
    public void AColdPrinterKeepsAReadableScale()
    {
        TemperatureChart chart = TemperatureChart.For(SeriesOf((0, 21, 22), (30, 23, 22)))!;

        chart.Ceiling.Should().BeGreaterThanOrEqualTo(60);
    }

    /// <summary>
    /// The gridlines stay few and round: this is a status graph, not an instrument.
    /// </summary>
    [Fact]
    public void GridlinesAreFewAndRound()
    {
        TemperatureChart chart = TemperatureChart.For(SeriesOf((0, 250, 100)))!;

        chart.TemperatureTicks.Should().HaveCountLessThanOrEqualTo(8);
        chart.TemperatureTicks.Select(tick => tick.Value % 5).Should().AllBeEquivalentTo(0d);
    }

    /// <summary>
    /// Hotter reads higher. Stated because the y axis grows downwards in SVG, which is exactly the
    /// kind of inversion that ships.
    /// </summary>
    [Fact]
    public void HotterSitsHigherOnThePage()
    {
        TemperatureChart chart = TemperatureChart.For(SeriesOf((0, 40, 40), (30, 240, 40)))!;

        (double x, double y)[] points = PointsOf(chart.NozzlePath);

        points[1].y.Should().BeLessThan(points[0].y);
    }

    /// <summary>
    /// The window's whole width is used, so the last point sits at the right edge rather than
    /// wherever the samples happened to stop.
    /// </summary>
    [Fact]
    public void TheWindowSpansThePlot()
    {
        TemperatureChart chart = TemperatureChart.For(SeriesOf((0, 100, 50), (60, 100, 50)))!;

        (double x, double y)[] points = PointsOf(chart.NozzlePath);

        points[0].x.Should().BeApproximately(TemperatureChart.BaselineLeft, 0.5);
        points[^1].x.Should().BeApproximately(TemperatureChart.BaselineRight, 0.5);
    }
}
