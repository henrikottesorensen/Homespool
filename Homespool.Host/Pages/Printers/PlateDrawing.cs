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
/// <b>Drawn in millimetres, with y turned over.</b> The viewBox is the bed - the file's own, else its
/// model's - widened to any object off it, so no scale is worked out here; the printer's y runs from the front of the bed to the back, and SVG's from the
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

    private PlateDrawing(IReadOnlyList<PlateItem> objects, PlateBounds? frame, PlateBounds? bed)
    {
        Objects = objects;
        Frame = frame;
        Bed = bed;
    }

    /// <summary>Every cancellable object, in id order.</summary>
    public IReadOnlyList<PlateItem> Objects { get; }

    /// <summary>
    /// What the drawing covers, or null when there is nothing to draw - no layout, or none of its
    /// outlines readable. The list is shown either way.
    /// </summary>
    public PlateBounds? Frame { get; }

    /// <summary>
    /// The bed to draw, or null when neither the file nor the printer's model says what it is - the
    /// drawing is then a box fitted around the objects, with no bed in it.
    /// </summary>
    public PlateBounds? Bed { get; }

    /// <summary>The SVG viewBox for <see cref="Frame"/>, invariant-formatted. Empty without one.</summary>
    public string ViewBox => Frame is not null ?
        string.Join(' ', Number(Frame.MinX), Number(Frame.MinY), Number(Frame.MaxX - Frame.MinX), Number(Frame.MaxY - Frame.MinY)) :
        string.Empty;

    /// <summary>
    /// The object numbers' size in user units - millimetres here - scaled to the frame, so a label
    /// reads the same on a small bed as on a large one.
    /// </summary>
    public double LabelSize => Frame is not null ? Math.Max(Frame.MaxX - Frame.MinX, Frame.MaxY - Frame.MinY) / 28 : 0;

    /// <summary>How many objects are cancelled.</summary>
    public int CancelledCount => Objects.Count(item => item.Cancelled);

    /// <summary>
    /// The plate for a print the printer reports <paramref name="objectCount"/> cancellable objects
    /// on.
    /// </summary>
    /// <param name="objectCount">The printer's own count.</param>
    /// <param name="cancelled">The ids the printer reports cancelled.</param>
    /// <param name="layout">The slicer's description of the plate, or null when none has been read.</param>
    /// <param name="modelBed">
    /// The bed of the printer's model, for a file that does not state its own - every
    /// <c>.bgcode</c>. The file's own statement wins where there is one, since it describes this print.
    /// </param>
    public static PlateDrawing For(int objectCount,
                                   IReadOnlySet<int> cancelled,
                                   PlateLayout? layout,
                                   PlateBounds? modelBed = null)
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

        if (!items.Any(item => item.Outline.Count > 0))
        {
            // Nothing to draw, so no drawing - a bed with nothing on it says nothing the list does not.
            return new PlateDrawing(items, frame: null, bed: null);
        }

        PlateBounds? bed = matched?.Bed ?? modelBed;

        // The bed and every object, so an object off the bed - a file sliced for another model - is
        // drawn where it is rather than clipped away.
        return new PlateDrawing(items, bed is null ? Fit(items) : Union(bed, Extent(items)), bed);
    }

    /// <summary>The smallest rectangle holding both.</summary>
    private static PlateBounds Union(PlateBounds a, PlateBounds b)
    {
        return new PlateBounds(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY),
                               Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
    }

    /// <summary>The rectangle holding every outline. Only called with at least one.</summary>
    private static PlateBounds Extent(IReadOnlyList<PlateItem> items)
    {
        List<PlatePoint> points = items.SelectMany(item => item.Outline).ToList();

        return new PlateBounds(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    /// <summary>A box around every outline with a margin. Only called with at least one.</summary>
    private static PlateBounds Fit(IReadOnlyList<PlateItem> items)
    {
        PlateBounds extent = Extent(items);

        double margin = Math.Max(MinimumMargin,
                                 Math.Max(extent.MaxX - extent.MinX, extent.MaxY - extent.MinY) * FitMargin);

        return new PlateBounds(extent.MinX - margin, extent.MinY - margin, extent.MaxX + margin, extent.MaxY + margin);
    }

    /// <summary>The path data for <paramref name="item"/>'s outline, y turned over. Empty without one.</summary>
    public string PathFor(PlateItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        PlateBounds? frame = Frame;

        if (frame is null || item.Outline.Count == 0)
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

        PlateBounds? frame = Frame;

        if (frame is null || item.Outline.Count == 0)
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
