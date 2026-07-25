using System.Diagnostics.CodeAnalysis;

namespace PrinterService.Host.PrusaConnect;

public class ToolMapping
{
    /// <remarks>
    /// A rectangular array because that is the shape the wire uses - a tool-to-slot grid, not a
    /// ragged one - so CA1814's jagged-array advice does not apply.
    /// </remarks>
    [SuppressMessage("Performance", "CA1814:Prefer jagged arrays over multidimensional",
                     Justification = "Models a fixed tool/slot grid from the wire format; rows are never ragged.")]
    public byte[,] Mapping { get; set; } = new byte[1, 1];
}
