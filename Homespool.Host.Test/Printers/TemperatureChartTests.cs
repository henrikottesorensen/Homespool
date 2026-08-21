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
    /// A printer with no chamber and no enclosure draws neither, rather than drawing them along the
    /// bottom - which is the difference between "not fitted" and "at zero degrees".
    /// </summary>
    [Fact]
    public void APrinterWithoutAChamberDrawsNeitherAmbientTrace()
    {
        TemperatureChart chart = TemperatureChart.For(SeriesOf((0, 200, 60), (30, 205, 60)))!;

        chart.ChamberPath.Should().BeEmpty();
        chart.TargetChamberPath.Should().BeEmpty();
        chart.EnclosurePath.Should().BeEmpty();
    }

    /// <summary>A chamber that is reported is drawn, along with what it was asked to reach.</summary>
    [Fact]
    public void AChamberIsDrawnWithItsSetpoint()
    {
        TemperatureSeries series = new(Start, Start.AddMinutes(60),
        [
            new TemperaturePoint(Start, 200, 60, 215, 60, Chamber: 30, TargetChamber: 40),
            new TemperaturePoint(Start.AddMinutes(30), 215, 60, 215, 60, Chamber: 38, TargetChamber: 40),
        ]);

        TemperatureChart chart = TemperatureChart.For(series)!;

        chart.ChamberPath.Should().NotBeEmpty();
        chart.TargetChamberPath.Should().NotBeEmpty();
        chart.EnclosurePath.Should().BeEmpty();
    }

    /// <summary>
    /// An enclosure is drawn and never carries a setpoint - the wire has no target for it.
    /// </summary>
    [Fact]
    public void AnEnclosureIsDrawnWithoutASetpoint()
    {
        TemperatureSeries series = new(Start, Start.AddMinutes(60),
        [
            new TemperaturePoint(Start, 200, 60, 215, 60, Enclosure: 28),
            new TemperaturePoint(Start.AddMinutes(30), 215, 60, 215, 60, Enclosure: 31),
        ]);

        TemperatureChart chart = TemperatureChart.For(series)!;

        chart.EnclosurePath.Should().NotBeEmpty();
        chart.ChamberPath.Should().BeEmpty();
        chart.TargetChamberPath.Should().BeEmpty();
    }

    /// <summary>
    /// A chamber hotter than either heater still fits under the axis. It cannot happen on hardware
    /// that exists, and an axis that clips a trace is a drawing that lies.
    /// </summary>
    [Fact]
    public void AHotChamberRaisesTheCeiling()
    {
        TemperatureSeries series = new(Start, Start.AddMinutes(60),
        [
            new TemperaturePoint(Start, 30, 30, 0, 0, Chamber: 250),
        ]);

        TemperatureChart.For(series)!.Ceiling.Should().BeGreaterThan(250);
    }

    /// <summary>
    /// A heater that is off draws no setpoint, rather than a dashed line along the axis.
    /// </summary>
    /// <remarks>
    /// Zero is off, not a request for zero degrees - the same reading <c>HeaterReading.For</c> takes
    /// of the same number. An idle printer used to draw three dashed lines on the floor of the graph.
    /// </remarks>
    [Fact]
    public void AZeroSetpointIsNotDrawn()
    {
        TemperatureSeries series = new(Start, Start.AddMinutes(60),
        [
            new TemperaturePoint(Start, 30, 25, 0, 0, Chamber: 24, TargetChamber: 0),
            new TemperaturePoint(Start.AddMinutes(30), 29, 24, 0, 0, Chamber: 24, TargetChamber: 0),
        ]);

        TemperatureChart chart = TemperatureChart.For(series)!;

        chart.TargetNozzlePath.Should().BeEmpty();
        chart.TargetBedPath.Should().BeEmpty();
        chart.TargetChamberPath.Should().BeEmpty();

        // The measurements are untouched - only the setpoints answer to this rule.
        chart.NozzlePath.Should().NotBeEmpty();
        chart.ChamberPath.Should().NotBeEmpty();
    }

    /// <summary>
    /// A setpoint that is switched off mid-window ends the dashed line rather than dropping it to the
    /// floor - which is what makes a dashed line mean "being aimed at right now".
    /// </summary>
    [Fact]
    public void ASetpointSwitchedOffEndsItsLine()
    {
        TemperatureSeries series = new(Start, Start.AddMinutes(60),
        [
            new TemperaturePoint(Start, 215, 60, 215, 60),
            new TemperaturePoint(Start.AddMinutes(20), 215, 60, 215, 60),
            new TemperaturePoint(Start.AddMinutes(40), 120, 40, 0, 0),
        ]);

        TemperatureChart chart = TemperatureChart.For(series)!;

        // Two aimed points, so one move and one line - and nothing after the heaters went off.
        chart.TargetNozzlePath.Count(character => character == 'M').Should().Be(1);
        chart.TargetNozzlePath.Count(character => character == 'L').Should().Be(1);
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
