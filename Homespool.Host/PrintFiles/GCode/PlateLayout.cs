using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// A print's plate as its slicer described it: each cancellable object's name and outline on the
/// bed, and the bed's extents where the file states them.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Objects"/> is positional, and that is the whole contract.</b> The object at index
/// <i>N</i> is the one the printer reports as id <i>N</i>: the slicer writes the header in the order
/// it declares the objects to the printer, and nothing else observable - name, position, print
/// order - follows that order. So an object that fails to parse is kept with no outline rather than
/// dropped, since dropping it would shift every object after it onto the wrong id.
/// </para>
/// <para>
/// <b>Read with limits, because none of it is the printer's.</b> Firmware relays the slicer's headers
/// without reading them, so this is whatever the file said - a name is only ever text, a coordinate
/// only ever a finite number within reach of a bed, and a plate past the limits is read as having no
/// layout at all rather than as part of one.
/// </para>
/// </remarks>
/// <param name="Objects">The objects, index <i>N</i> being object id <i>N</i>.</param>
/// <param name="Bed">The bed's extents, or null when the file does not state them - every <c>.bgcode</c>.</param>
public sealed record PlateLayout(IReadOnlyList<PlateObject> Objects, PlateBounds? Bed)
{
    /// <summary>The most objects read, matching firmware's own ceiling on cancellable objects.</summary>
    public const int MaxObjects = 1024;

    /// <summary>The most corners one outline may have before it is read as having none.</summary>
    /// <remarks>An organic model's outline measured about a hundred.</remarks>
    public const int MaxOutlinePoints = 2048;

    /// <summary>The most corners across the whole plate before it is read as having no layout.</summary>
    public const int MaxTotalPoints = 65_536;

    /// <summary>The longest object name kept. A longer one is read as unnamed, not cut short.</summary>
    public const int MaxNameLength = 200;

    /// <summary>How far from the origin, in millimetres, a coordinate may be. No bed is ten metres across.</summary>
    public const double MaxCoordinate = 10_000;

    /// <summary>The corners a bed shape may list. A rectangle is four; a round bed is drawn with more.</summary>
    private const int MaxBedPoints = 256;

    /// <summary>
    /// The layout <paramref name="objectsInfo"/> and <paramref name="bedShape"/> describe, or null
    /// when <paramref name="objectsInfo"/> is absent or cannot be read as a plate.
    /// </summary>
    /// <param name="objectsInfo">The <c>objects_info</c> header: JSON, <c>{"objects":[{"name":…,"polygon":[[x,y],…]}]}</c>.</param>
    /// <param name="bedShape">The <c>bed_shape</c> header, <c>"0x0,250x0,250x210,0x210"</c>, or null.</param>
    public static PlateLayout? Parse(string? objectsInfo, string? bedShape)
    {
        if (string.IsNullOrWhiteSpace(objectsInfo))
        {
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(objectsInfo, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("objects", out JsonElement objects) ||
                objects.ValueKind != JsonValueKind.Array ||
                objects.GetArrayLength() > MaxObjects)
            {
                return null;
            }

            List<PlateObject> read = [];
            int totalPoints = 0;

            foreach (JsonElement entry in objects.EnumerateArray())
            {
                IReadOnlyList<PlatePoint> outline = ReadOutline(entry);

                totalPoints += outline.Count;

                if (totalPoints > MaxTotalPoints)
                {
                    return null;
                }

                read.Add(new PlateObject(read.Count, ReadName(entry), outline));
            }

            return new PlateLayout(read, ParseBed(bedShape));
        }
    }

    /// <summary>
    /// The extents a <c>bed_shape</c> header describes, or null when it is absent or unreadable.
    /// </summary>
    /// <remarks>
    /// The bounds rather than the shape: every bed here is a rectangle, and a round one drawn as its
    /// square is still the right frame for the objects on it.
    /// </remarks>
    public static PlateBounds? ParseBed(string? bedShape)
    {
        if (string.IsNullOrWhiteSpace(bedShape))
        {
            return null;
        }

        string[] corners = bedShape.Split(',', StringSplitOptions.TrimEntries);

        if (corners.Length is < 3 or > MaxBedPoints)
        {
            return null;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        foreach (string corner in corners)
        {
            string[] pair = corner.Split('x');

            if (pair.Length != 2 ||
                !TryCoordinate(pair[0], out double x) ||
                !TryCoordinate(pair[1], out double y))
            {
                return null;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return maxX > minX && maxY > minY ? new PlateBounds(minX, minY, maxX, maxY) : null;
    }

    private static bool TryCoordinate(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               Within(value);
    }

    private static bool Within(double value)
    {
        return double.IsFinite(value) && Math.Abs(value) <= MaxCoordinate;
    }

    /// <summary>
    /// The object's name, or null where it has none worth showing.
    /// </summary>
    /// <remarks>
    /// <b>Percent-decoded</b>, because PrusaSlicer writes a name taken from a file name that way -
    /// <c>GF%20dremel%20bit%20storage.stl</c> - and the escaped form is not what anybody called it.
    /// </remarks>
    private static string? ReadName(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? name = nameElement.GetString();

        if (name is null)
        {
            return null;
        }

        try
        {
            name = Uri.UnescapeDataString(name);
        }
        catch (UriFormatException)
        {
            // Kept as written. The escape is a courtesy, and a name that is not a valid one is still
            // a name.
        }

        name = name.Trim();

        return name.Length is 0 or > MaxNameLength || ContainsControl(name) ? null : name;
    }

    private static bool ContainsControl(string text)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The object's outline, or empty where it has none that can be drawn - never a partial one,
    /// which would draw a shape the slicer did not describe.
    /// </summary>
    private static IReadOnlyList<PlatePoint> ReadOutline(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("polygon", out JsonElement polygon) ||
            polygon.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        int length = polygon.GetArrayLength();

        if (length is < 3 or > MaxOutlinePoints)
        {
            return [];
        }

        List<PlatePoint> points = new(length);

        foreach (JsonElement corner in polygon.EnumerateArray())
        {
            if (corner.ValueKind != JsonValueKind.Array ||
                corner.GetArrayLength() != 2 ||
                !TryNumber(corner[0], out double x) ||
                !TryNumber(corner[1], out double y))
            {
                return [];
            }

            points.Add(new PlatePoint(x, y));
        }

        return points;
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        value = 0;

        return element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out value) &&
               Within(value);
    }
}

/// <summary>One object on a plate.</summary>
/// <param name="Id">The object's id, 0-based - its position in the slicer's list.</param>
/// <param name="Name">What the slicer called it, or null when it gave no usable name.</param>
/// <param name="Outline">Its footprint on the bed in millimetres, or empty when there is none to draw.</param>
public sealed record PlateObject(int Id, string? Name, IReadOnlyList<PlatePoint> Outline);

/// <summary>A point on the bed, in millimetres from the printer's origin.</summary>
/// <param name="X">Across the bed.</param>
/// <param name="Y">Front to back - up, in the printer's frame, which is not the SVG's.</param>
public sealed record PlatePoint(double X, double Y);

/// <summary>A rectangle on the bed, in millimetres.</summary>
/// <param name="MinX">The left edge.</param>
/// <param name="MinY">The front edge.</param>
/// <param name="MaxX">The right edge.</param>
/// <param name="MaxY">The back edge.</param>
public sealed record PlateBounds(double MinX, double MinY, double MaxX, double MaxY);
