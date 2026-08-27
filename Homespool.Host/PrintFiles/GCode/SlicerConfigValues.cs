using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Homespool.Host.PrintFiles.GCode;

/// <summary>
/// Turns the slicer's <c>key</c>/<c>value</c> pairs into a <see cref="GCodeMetadata"/>, whichever
/// container they arrived in.
/// </summary>
/// <remarks>
/// <para>
/// <b>One accumulator for both encodings</b>, because the two differ only in packaging: a binary
/// file's Printer Metadata block is <c>key=value</c> lines, and an ASCII file's trailing
/// configuration block is <c>; key = value</c> comment lines, carrying the same names and the same
/// spellings. Everything downstream of the split is identical, and having it in one place is what
/// keeps the two readers from drifting apart.
/// </para>
/// <para>
/// <b>A value that will not parse is dropped whole, never in part.</b> The lists are positional -
/// entry <i>n</i> describes extruder <i>n</i> - so a list with one unreadable element cannot be
/// salvaged by skipping that element: every later entry would silently describe the wrong extruder.
/// Dropping the key yields "the file did not say", which the comparison already handles.
/// </para>
/// </remarks>
internal sealed class SlicerConfigValues
{
    /// <summary>The five keys worth reading, and nothing else.</summary>
    /// <remarks>
    /// Deliberately not "parse the whole configuration block". These five are what decides whether
    /// the printer in front of us can print the file; the rest of a 15 KB block is the slicer's
    /// business, and parsing it would invite storing it.
    /// </remarks>
    public const string PrinterModelKey = "printer_model";

    public const string NozzleDiameterKey = "nozzle_diameter";

    public const string FilamentTypeKey = "filament_type";

    public const string FilamentAbrasiveKey = "filament_abrasive";

    public const string NozzleHighFlowKey = "nozzle_high_flow";

    private string? _printerModel;
    private IReadOnlyList<float>? _nozzleDiameters;
    private IReadOnlyList<string>? _filamentTypes;
    private IReadOnlyList<bool>? _filamentAbrasive;
    private IReadOnlyList<bool>? _nozzleHighFlow;

    /// <summary>Whether every key this cares about has been seen, so a scan can stop early.</summary>
    public bool Complete => _printerModel is not null
                            && _nozzleDiameters is not null
                            && _filamentTypes is not null
                            && _filamentAbrasive is not null
                            && _nozzleHighFlow is not null;

    /// <summary>
    /// Offers one pair. Keys this does not care about, and values that will not parse, are ignored.
    /// </summary>
    /// <remarks>
    /// <b>First wins.</b> A key cannot legitimately appear twice, so a second occurrence is either
    /// corruption or somebody appending their own block to a sliced file; taking the first keeps the
    /// answer the slicer's.
    /// </remarks>
    public void Accept(string key, string value)
    {
        switch (key)
        {
            case PrinterModelKey when _printerModel is null:
                string model = Unquote(value.Trim());

                if (model.Length > 0)
                {
                    _printerModel = model;
                }

                break;

            case NozzleDiameterKey when _nozzleDiameters is null:
                _nozzleDiameters = ParseFloats(value);

                break;

            case FilamentTypeKey when _filamentTypes is null:
                _filamentTypes = ParseStrings(value);

                break;

            case FilamentAbrasiveKey when _filamentAbrasive is null:
                _filamentAbrasive = ParseBools(value);

                break;

            case NozzleHighFlowKey when _nozzleHighFlow is null:
                _nozzleHighFlow = ParseBools(value);

                break;
        }
    }

    /// <summary>What was gathered, with anything unseen left empty.</summary>
    public GCodeMetadata Build()
    {
        return new GCodeMetadata(_printerModel,
                                 _nozzleDiameters ?? [],
                                 _filamentTypes ?? [],
                                 _filamentAbrasive ?? [],
                                 _nozzleHighFlow ?? []);
    }

    /// <summary>
    /// Splits one <c>key</c>/<c>value</c> line on its first <c>=</c>, or false if there is none.
    /// </summary>
    /// <remarks>
    /// The first <c>=</c> rather than the only one: values contain them - <c>objects_info</c> is a
    /// JSON string, and <c>start_gcode</c> is a whole program.
    /// </remarks>
    public static bool TrySplit(string line, out string key, out string value)
    {
        int separator = line.IndexOf('=', StringComparison.Ordinal);

        if (separator < 0)
        {
            key = string.Empty;
            value = string.Empty;

            return false;
        }

        key = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();

        return key.Length > 0;
    }

    /// <summary>
    /// Millimetres, comma-separated - PrusaSlicer's serialisation for a numeric vector option.
    /// </summary>
    private static IReadOnlyList<float>? ParseFloats(string value)
    {
        string[] parts = value.Split(',');
        List<float> parsed = new(parts.Length);

        foreach (string part in parts)
        {
            // Invariant, not the ambient culture: the slicer writes a point regardless of where the
            // machine that ran it thinks the decimal separator is.
            if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
            {
                return null;
            }

            parsed.Add(number);
        }

        return parsed;
    }

    /// <summary>
    /// <c>0</c>/<c>1</c>, comma-separated - PrusaSlicer's serialisation for a boolean vector option.
    /// </summary>
    private static IReadOnlyList<bool>? ParseBools(string value)
    {
        string[] parts = value.Split(',');
        List<bool> parsed = new(parts.Length);

        foreach (string part in parts)
        {
            switch (part.Trim())
            {
                case "0":
                    parsed.Add(false);

                    break;

                case "1":
                    parsed.Add(true);

                    break;

                default:
                    return null;
            }
        }

        return parsed;
    }

    /// <summary>
    /// Semicolon-separated strings, quoted where a value contains something a split would eat.
    /// </summary>
    /// <remarks>
    /// The separator differs from the numeric one, which is not an inconsistency to normalise away:
    /// PrusaSlicer's <c>escape_strings_cstyle</c> joins with <c>;</c> precisely so that a value may
    /// contain a comma. Stock filament names never need the quoting; a user-named one can.
    /// </remarks>
    private static IReadOnlyList<string>? ParseStrings(string value)
    {
        List<string> parsed = [];
        StringBuilder current = new();
        bool quoted = false;
        bool escaped = false;

        foreach (char character in value)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
            }
            else if (character == '\\' && quoted)
            {
                escaped = true;
            }
            else if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ';' && !quoted)
            {
                parsed.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted || escaped)
        {
            // Unterminated: the value is not what it claims to be, so it says nothing rather than
            // something half-read.
            return null;
        }

        parsed.Add(current.ToString().Trim());

        return parsed;
    }

    /// <summary>Strips one layer of surrounding quotes from a scalar value.</summary>
    private static string Unquote(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }
}
