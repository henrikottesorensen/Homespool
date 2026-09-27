using System.Collections.Generic;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// The bed each Prusa model prints on, for drawing a plate whose file does not state one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Copied from PrusaSlicer's own printer profiles</b> - <c>resources/profiles/PrusaResearch.ini</c>
/// at tag <c>version_2.9.6</c> (<c>b028299c77</c>), each model's <c>bed_shape</c> resolved through
/// its preset's <c>inherits</c>. They are the numbers the slicer writes into a plain
/// <c>.gcode</c>'s own <c>bed_shape</c> header, so a <c>.bgcode</c>, which does not relay that
/// header, is drawn on the same bed a <c>.gcode</c> of the same print would be. A 3.0 alpha
/// reorganises the profiles and is not the source.
/// </para>
/// <para>
/// <b>Keyed on the firmware's version triple</b>, the value <c>Printer.Model</c> holds, with the name
/// beside it: a test holds each name against <see cref="Model.PrinterModelNames"/>, so a triple
/// that ever meant something else would fail there rather than draw the wrong bed.
/// </para>
/// <para>
/// <b>A model with no preset at that tag has no entry</b> - the iX, both CORE One INDX models and the
/// XLP - and its plate is framed around the objects instead. The MK3.5S has one because the
/// slicer's presets cover it by name ("Original Prusa MK3.5 &amp; MK3.5S").
/// </para>
/// </remarks>
public static class PrusaBedShapes
{
    /// <summary>Every entry: the firmware's triple, the model it names, and the slicer's <c>bed_shape</c>.</summary>
    public static IReadOnlyList<(string printerType, string name, string bedShape)> Entries { get; } =
    [
        ("1.3.0", "MK3", "0x0,250x0,250x210,0x210"),
        ("1.3.1", "MK3S", "0x0,250x0,250x210,0x210"),
        ("1.3.5", "MK3.5", "0x0,250x0,250x210,0x210"),
        ("1.3.6", "MK3.5S", "0x0,250x0,250x210,0x210"),
        ("1.3.9", "MK3.9", "0x0,250x0,250x210,0x210"),
        ("1.3.10", "MK3.9S", "0x0,250x0,250x210,0x210"),
        ("1.4.0", "MK4", "0x0,250x0,250x210,0x210"),
        ("1.4.1", "MK4S", "0x0,250x0,250x210,0x210"),
        ("2.1.0", "MINI", "0x0,180x0,180x180,0x180"),
        ("3.1.0", "XL", "0x0,360x0,360x360,0x360"),
        ("5.1.0", "XL", "0x0,360x0,360x360,0x360"),
        ("7.1.0", "COREONE", "0x0,250x0,250x220,0x220"),
        ("7.2.0", "COREONEOAK", "0x0,250x0,250x220,0x220"),
        ("8.1.0", "COREONEL", "0x0,300x0,300x300,0x300"),
    ];

    /// <summary>
    /// The bed of the model a printer reports as <paramref name="printerType"/>, or null for a model
    /// with no entry, or none reported.
    /// </summary>
    public static PlateBounds? For(string? printerType)
    {
        foreach ((string type, string _, string bedShape) in Entries)
        {
            if (string.Equals(type, printerType, System.StringComparison.Ordinal))
            {
                return PlateLayout.ParseBed(bedShape);
            }
        }

        return null;
    }
}
