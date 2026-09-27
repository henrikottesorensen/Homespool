using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Homespool.Host.PrintFiles.GCode;

namespace Homespool.Host.Pages.Printers;

/// <summary>
/// The printer page's plate: every cancellable object of the running print, whether it is cancelled,
/// and where it sits on the bed - worked out where it can be tested, as the temperature graph is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The printer's count decides which objects exist; the file only describes them.</b> The list is
/// built from the count the printer last reported, and names and outlines are laid over it from the
/// slicer's header when the two agree on how many objects there are. When they do not, the header is
/// not used at all, because the join between them is positional: a list one short would put every
/// name after the gap on its neighbour's button.
/// </para>
/// <para>
/// <b>Drawn in millimetres, with y turned over.</b> The viewBox is the bed itself, so no scale is
/// worked out here; the printer's y runs from the front of the bed to the back, and SVG's from the
/// top of the drawing down, so the back of the bed is drawn at the top - which is how the bed looks
/// from the front of the machine, and how the slicer shows it.
/// </para>
/// <para>
/// <b>Numbered from one, as the printer's own menu is</b>, so somebody looking at both screens sees
/// the same numbers. The id underneath stays the 0-based one the printer reports.
/// </para>
/// </remarks>
public sealed class PlateDrawing
{
    /// <summary>
    /// The margin around the objects when the file does not state the bed, as a share of their
    /// extent - enough that an outline does not touch the frame.
    /// </summary>
    private const double FitMargin = 0.08;

    /// <summary>The smallest margin, in millimetres, so one small object is not drawn edge to edge.</summary>
    private const double MinimumMargin = 5;

    private PlateDrawing(IReadOnlyList<PlateItem> objects, PlateBounds? frame, bool bedStated)
    {
        Objects = objects;
        Frame = frame;
        BedStated = bedStated;
    }

    /// <summary>Every cancellable object, in id order.</summary>
    public IReadOnlyList<PlateItem> Objects { get; }

    /// <summary>
    /// What the drawing covers, or null when there is nothing to draw - no layout, or none of its
    /// outlines readable. The list is shown either way.
    /// </summary>
    public PlateBounds? Frame { get; }

    /// <summary>
    /// Whether <see cref="Frame"/> is the bed as the file states it, and so worth drawing as a bed,
    /// rather than a box fitted around the objects.
    /// </summary>
    public bool BedStated { get; }

    /// <summary>The SVG viewBox for <see cref="Frame"/>, invariant-formatted. Empty without one.</summary>
    public string ViewBox => Frame is { } frame ?
        string.Join(' ', Number(frame.MinX), Number(frame.MinY), Number(frame.MaxX - frame.MinX), Number(frame.MaxY - frame.MinY)) :
        string.Empty;

    /// <summary>
    /// The object numbers' size in user units - millimetres here - scaled to the frame, so a label
    /// reads the same on a small bed as on a large one.
    /// </summary>
    public double LabelSize => Frame is { } frame ? Math.Max(frame.MaxX - frame.MinX, frame.MaxY - frame.MinY) / 28 : 0;

    /// <summary>How many objects are cancelled.</summary>
    public int CancelledCount => Objects.Count(item => item.Cancelled);

    /// <summary>
    /// The plate for a print the printer reports <paramref name="objectCount"/> cancellable objects
    /// on.
    /// </summary>
    /// <param name="objectCount">The printer's own count.</param>
    /// <param name="cancelled">The ids the printer reports cancelled.</param>
    /// <param name="layout">The slicer's description of the plate, or null when none has been read.</param>
    public static PlateDrawing For(int objectCount, IReadOnlySet<int> cancelled, PlateLayout? layout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentNullException.ThrowIfNull(cancelled);

        // Positional, so only when the two agree - see the remarks.
        PlateLayout? matched = layout?.Objects.Count == objectCount ? layout : null;

        List<PlateItem> items = new(objectCount);

        for (int id = 0; id < objectCount; id++)
        {
            PlateObject? described = matched?.Objects[id];
            IReadOnlyList<PlatePoint> outline = described?.Outline ?? [];

            items.Add(new PlateItem(id, described?.Name, cancelled.Contains(id), outline));
        }

        PlateBounds? frame = matched?.Bed ?? Fit(items);

        return new PlateDrawing(items, items.Any(item => item.Outline.Count > 0) ? frame : null, matched?.Bed is not null);
    }

    /// <summary>A box around every outline with a margin, or null when there are none.</summary>
    private static PlateBounds? Fit(IReadOnlyList<PlateItem> items)
    {
        List<PlatePoint> points = items.SelectMany(item => item.Outline).ToList();

        if (points.Count == 0)
        {
            return null;
        }

        double minX = points.Min(p => p.X);
        double maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y);
        double maxY = points.Max(p => p.Y);

        double margin = Math.Max(MinimumMargin, Math.Max(maxX - minX, maxY - minY) * FitMargin);

        return new PlateBounds(minX - margin, minY - margin, maxX + margin, maxY + margin);
    }

    /// <summary>The path data for <paramref name="item"/>'s outline, y turned over. Empty without one.</summary>
    public string PathFor(PlateItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Frame is not { } frame || item.Outline.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder path = new();

        for (int index = 0; index < item.Outline.Count; index++)
        {
            PlatePoint point = item.Outline[index];

            path.Append(index == 0 ? 'M' : 'L')
                .Append(Number(point.X))
                .Append(' ')
                .Append(Number(Flip(frame, point.Y)))
                .Append(' ');
        }

        return path.Append('Z').ToString();
    }

    /// <summary>Where <paramref name="item"/>'s number goes: the middle of its outline, y turned over.</summary>
    public (string x, string y) LabelFor(PlateItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (Frame is not { } frame || item.Outline.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        double x = (item.Outline.Min(p => p.X) + item.Outline.Max(p => p.X)) / 2;
        double y = (item.Outline.Min(p => p.Y) + item.Outline.Max(p => p.Y)) / 2;

        return (Number(x), Number(Flip(frame, y)));
    }

    /// <summary>A bed y as an SVG y: the back of the bed at the top.</summary>
    private static double Flip(PlateBounds frame, double y)
    {
        return frame.MaxY + frame.MinY - y;
    }

    /// <summary>SVG parses only a full stop, whatever the reader's culture.</summary>
    private static string Number(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

/// <summary>One object on the plate, as the page shows it.</summary>
/// <param name="Id">The printer's id for it, 0-based. What a form posts.</param>
/// <param name="Name">What the slicer called it, or null when the file gave no usable name.</param>
/// <param name="Cancelled">Whether the printer reports it cancelled.</param>
/// <param name="Outline">Its footprint in bed millimetres, or empty when there is none to draw.</param>
public sealed record PlateItem(int Id, string? Name, bool Cancelled, IReadOnlyList<PlatePoint> Outline)
{
    /// <summary>The number a person sees, 1-based as the printer's menu counts.</summary>
    public int Number => Id + 1;
}
