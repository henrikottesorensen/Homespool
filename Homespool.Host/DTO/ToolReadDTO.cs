using System;

using Homespool.Host.Printing;

namespace Homespool.Host.DTO;

/// <summary>One tool: what is loaded, how hot it is, and what nozzle it has.</summary>
/// <remarks>
/// <b><see cref="ToolNumber"/> is the printer's, counting from one, and not necessarily contiguous</b> -
/// a toolchanger may report 1, 2 and 5. Find a tool by its number, never by its place in the list.
/// </remarks>
public class ToolReadDTO
{
    public required int ToolNumber { get; set; }

    /// <summary>What is loaded, or null when the tool is empty.</summary>
    public string? Material { get; set; }

    /// <summary>This tool's own nozzle reading, °C, where it reports one.</summary>
    public float? Temperature { get; set; }

    /// <summary>Whether this tool is on the carriage. Only meaningful on a toolchanger; false on a single-tool printer.</summary>
    public required bool Picked { get; set; }

    /// <summary>Millimetres, where the printer has described its nozzle.</summary>
    public float? NozzleDiameter { get; set; }

    public required bool Hardened { get; set; }

    public static ToolReadDTO FromState(PrinterToolState tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return new()
        {
            ToolNumber = tool.ToolNumber,
            Material = tool.Material,
            Temperature = tool.Temperature,
            Picked = tool.IsPicked,
            NozzleDiameter = tool.NozzleDiameter,
            Hardened = tool.Hardened,
        };
    }
}
