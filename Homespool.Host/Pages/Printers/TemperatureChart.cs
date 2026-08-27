using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Homespool.Host.Telemetry;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// The geometry of the printer page's temperature graph - everything the SVG needs, worked out
/// where it can be tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drawn by hand rather than by a charting library, and not for the sake of it.</b> Every script
/// and stylesheet this deployment serves comes from this deployment - a box can be internet-facing
/// and still have no route outward, so a CDN is not available and nothing is bundled.
/// Two lines and a pair of axes is a smaller thing to write
/// than a library is to vendor.
/// </para>
/// <para>
/// <b>Rendered on the server, which is what keeps the numbers honest.</b> Tick labels are values
/// rather than strings here, so the view formats them in the reader's culture - a comma decimal
/// separator in <c>da</c> - while the path coordinates below are built with
/// <see cref="CultureInfo.InvariantCulture"/>, because SVG only parses a full stop. Getting those
/// two the wrong way round produces a graph that draws nothing in Danish and reads wrong in English.
/// </para>
/// </remarks>
public sealed class TemperatureChart
{
    /// <summary>The viewBox width. The SVG itself scales; this is the coordinate space.</summary>
    public const double Width = 720;

    /// <summary>The viewBox height.</summary>
    public const double Height = 220;

    /// <summary>
    /// Left inset, wide enough for a three-digit temperature label.
    /// </summary>
    /// <remarks>
    /// Sized for the <em>largest</em> the label ever is, which is on a phone: the axis font is raised
    /// in user units under a narrow-screen media query so it stays readable once the whole drawing has
    /// been scaled down, and at 38 the "200" hung off the left edge and rendered as "00".
    /// </remarks>
    private const double PlotLeft = 48;

    private const double PlotRight = Width - 8;

    private const double PlotTop = 10;

    /// <summary>Bottom inset, leaving a line for the time labels under the axis.</summary>
    private const double PlotBottom = Height - 22;

    /// <summary>
    /// The coldest the axis ever tops out at, so a printer sitting at room temperature still gets a
    /// scale a person recognises rather than one stretched over eight degrees of drift.
    /// </summary>
    private const double MinimumCeiling = 60;

    /// <summary>
    /// Roughly how many horizontal gridlines to aim for.
    /// </summary>
    /// <remarks>
    /// Five rather than four, which is not a rounding preference. The step is chosen as the first
    /// round number at or above <c>ceiling / this</c>, so it always lands on the coarse side - at four
    /// a 220-degree axis asked for 55 and got 100, leaving a nozzle trace with two gridlines under it.
    /// </remarks>
    private const int TargetTickCount = 5;

    private TemperatureChart(double ceiling,
                             IReadOnlyList<AxisTick> temperatureTicks,
                             IReadOnlyList<TimeTick> timeTicks,
                             string nozzlePath,
                             string bedPath,
                             string targetNozzlePath,
                             string targetBedPath,
                             string chamberPath,
                             string targetChamberPath,
                             string enclosurePath)
    {
        Ceiling = ceiling;
        TemperatureTicks = temperatureTicks;
        TimeTicks = timeTicks;
        NozzlePath = nozzlePath;
        BedPath = bedPath;
        TargetNozzlePath = targetNozzlePath;
        TargetBedPath = targetBedPath;
        ChamberPath = chamberPath;
        TargetChamberPath = targetChamberPath;
        EnclosurePath = enclosurePath;
    }

    /// <summary>The top of the temperature axis, in degrees.</summary>
    public double Ceiling { get; }

    /// <summary>Where the horizontal gridlines go, and what each one is worth.</summary>
    public IReadOnlyList<AxisTick> TemperatureTicks { get; }

    /// <summary>Where the time labels go along the bottom.</summary>
    public IReadOnlyList<TimeTick> TimeTicks { get; }

    /// <summary>The measured nozzle trace, as an SVG path.</summary>
    public string NozzlePath { get; }

    /// <summary>The measured bed trace.</summary>
    public string BedPath { get; }

    /// <summary>The nozzle setpoint, drawn dashed behind its measurement.</summary>
    public string TargetNozzlePath { get; }

    /// <summary>The bed setpoint.</summary>
    public string TargetBedPath { get; }

    /// <summary>
    /// The chamber trace, empty on a printer without a managed chamber - which is most of them.
    /// </summary>
    /// <remarks>
    /// <b>Empty rather than flat.</b> A printer that reports no chamber is not a printer whose
    /// chamber is at zero, and the view draws nothing at all rather than a line along the axis.
    /// </remarks>
    public string ChamberPath { get; }

    /// <summary>The chamber setpoint. Empty unless the printer both has a chamber and aims it.</summary>
    public string TargetChamberPath { get; }

    /// <summary>
    /// The enclosure trace, empty unless an enclosure is fitted. Never accompanied by a setpoint -
    /// the wire carries no target for it.
    /// </summary>
    public string EnclosurePath { get; }

    /// <summary>The y of the axis itself, for the view to draw the baseline.</summary>
    public static double BaselineY => PlotBottom;

    /// <summary>The left edge of the plot, for the view to draw the baseline.</summary>
    public static double BaselineLeft => PlotLeft;

    /// <summary>The right edge of the plot.</summary>
    public static double BaselineRight => PlotRight;

    /// <summary>
    /// Works out the drawing, or returns null when there is nothing to draw.
    /// </summary>
    /// <remarks>
    /// <b>Null means "no temperature was reported in this window"</b> - a printer that is switched
    /// off, or has never connected. It is distinct from a flat line at zero, which would be a claim
    /// about a machine nobody heard from, and the page says so in words instead.
    /// </remarks>
    public static TemperatureChart? For(TemperatureSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        double span = (series.To - series.From).TotalMilliseconds;

        if (series.Points.Count == 0 || span <= 0)
        {
            return null;
        }

        double hottest = series.Points
                               .SelectMany(point => new[]
                               {
                                   point.Nozzle, point.Bed, point.TargetNozzle, point.TargetBed,
                                   point.Chamber, point.TargetChamber, point.Enclosure,
                               })
                               .Where(value => value.HasValue)
                               .Select(value => value!.Value)
                               .DefaultIfEmpty(double.NaN)
                               .Max();

        if (double.IsNaN(hottest))
        {
            return null;
        }

        double ceiling = Ceil(Math.Max(hottest, MinimumCeiling));
        double step = TickStep(ceiling);

        List<AxisTick> temperatureTicks = [];

        for (double value = 0; value <= ceiling + 0.001; value += step)
        {
            temperatureTicks.Add(new AxisTick(value, YFor(value, ceiling)));
        }

        return new TemperatureChart(
            ceiling,
            temperatureTicks,
            TimeTicksFor(series),
            PathFor(series, span, ceiling, point => point.Nozzle),
            PathFor(series, span, ceiling, point => point.Bed),
            PathFor(series, span, ceiling, point => Aimed(point.TargetNozzle)),
            PathFor(series, span, ceiling, point => Aimed(point.TargetBed)),
            PathFor(series, span, ceiling, point => point.Chamber),
            PathFor(series, span, ceiling, point => Aimed(point.TargetChamber)),
            PathFor(series, span, ceiling, point => point.Enclosure));
    }

    /// <summary>
    /// A setpoint, or null where there is none to draw.
    /// </summary>
    /// <remarks>
    /// <b>Zero is off, not a request for zero degrees</b> - the same reading
    /// <see cref="HeaterReading.For"/> already takes of the same number, and the graph disagreed with
    /// it: every idle stretch drew three dashed lines along the axis, which is clutter claiming to be
    /// a setpoint. Lifting the pen instead means a dashed line on this graph always marks something
    /// actually being aimed at, and the line simply ending is as legible as it falling to the floor.
    /// </remarks>
    private static double? Aimed(double? target)
    {
        return target is > 0 ? target : null;
    }

    /// <summary>
    /// One trace, as an SVG path.
    /// </summary>
    /// <remarks>
    /// <b>A gap breaks the line rather than being drawn across.</b> A null bucket is a stretch where
    /// the printer said nothing - a reconnection, a slim message run - and joining the two ends would
    /// draw a temperature it never had. So each run of readings starts a fresh <c>M</c>, which is
    /// also why this is a path and not a <c>polyline</c>: a polyline cannot lift the pen.
    /// </remarks>
    private static string PathFor(TemperatureSeries series,
                                  double span,
                                  double ceiling,
                                  Func<TemperaturePoint, double?> select)
    {
        StringBuilder path = new();
        bool penDown = false;

        foreach (TemperaturePoint point in series.Points)
        {
            if (select(point) is not { } value)
            {
                penDown = false;

                continue;
            }

            double x = PlotLeft + (((point.At - series.From).TotalMilliseconds / span) * (PlotRight - PlotLeft));
            double y = YFor(value, ceiling);

            path.Append(penDown ? 'L' : 'M')
                .Append(x.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(y.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(' ');

            penDown = true;
        }

        return path.ToString().TrimEnd();
    }

    /// <summary>
    /// Where the time labels go. Four across, including both ends, so the window's length is
    /// readable without a caption saying what it is.
    /// </summary>
    /// <remarks>
    /// <b>The two outermost labels are anchored to their own edge rather than centred.</b> A centred
    /// label at the right-hand end hangs half its width outside the viewBox and is clipped - seen
    /// rendering as <c>21:2</c>, which reads as a bad time rather than as a cropped one.
    /// </remarks>
    private static IReadOnlyList<TimeTick> TimeTicksFor(TemperatureSeries series)
    {
        const int Divisions = 3;

        List<TimeTick> ticks = [];

        for (int index = 0; index <= Divisions; index++)
        {
            double fraction = (double)index / Divisions;

            string anchor = index switch
            {
                0 => "start",
                Divisions => "end",
                _ => "middle",
            };

            ticks.Add(new TimeTick(
                series.From + ((series.To - series.From) * fraction),
                PlotLeft + (fraction * (PlotRight - PlotLeft)),
                anchor));
        }

        return ticks;
    }

    private static double YFor(double value, double ceiling)
    {
        double clamped = Math.Clamp(value, 0, ceiling);

        return PlotBottom - ((clamped / ceiling) * (PlotBottom - PlotTop));
    }

    /// <summary>Rounds the axis top up to something a person would have chosen.</summary>
    private static double Ceil(double hottest)
    {
        // A hair above the highest reading, so a nozzle sitting exactly on 250 does not push the
        // axis to 300 and squash the whole trace into the bottom five sixths of the box.
        return Math.Ceiling((hottest + 5) / 20) * 20;
    }

    /// <summary>The gridline spacing, from a short list so the labels stay round numbers.</summary>
    private static double TickStep(double ceiling)
    {
        double ideal = ceiling / TargetTickCount;

        foreach (double candidate in (double[])[10, 20, 25, 50, 100, 200])
        {
            if (candidate >= ideal)
            {
                return candidate;
            }
        }

        return 200;
    }

    /// <summary>A horizontal gridline: what it is worth, and where it sits.</summary>
    /// <param name="Value">Degrees, for the view to format in the reader's culture.</param>
    /// <param name="Y">Its position in the viewBox.</param>
    public sealed record AxisTick(double Value, double Y);

    /// <summary>A time label along the bottom.</summary>
    /// <param name="At">The instant, for the view to format in the reader's culture and zone.</param>
    /// <param name="X">Its position in the viewBox.</param>
    /// <param name="Anchor">
    /// Its SVG <c>text-anchor</c> - <c>start</c> at the left edge, <c>end</c> at the right, so neither
    /// end label is clipped by the viewBox it is centred on.
    /// </param>
    public sealed record TimeTick(DateTimeOffset At, double X, string Anchor);
}
