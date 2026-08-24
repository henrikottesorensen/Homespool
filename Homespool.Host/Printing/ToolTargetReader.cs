using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.PrusaConnect;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// Reads which tool a toolless gcode command would act on, for one printer.
/// </summary>
/// <remarks>
/// <b>Two columns rather than one, and both are needed</b> - see <see cref="ToolTarget.For"/>.
/// <c>ActiveSlot</c> answers picked-versus-unpicked but is absent on single-tool printers, because it
/// rides in a slot block firmware sends only when there is more than one tool; the <c>INFO</c> tool
/// rows answer how many there are but say nothing about which is live.
/// </remarks>
public class ToolTargetReader
{
    private readonly HomespoolDbContext _dbContext;

    public ToolTargetReader(HomespoolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Every tool a printer has told us about, in reported order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One list whatever the machine is.</b> A toolchanger's rows come from its slot block; a
    /// single-tool printer sends none, so its one tool is synthesised from the flat telemetry fields.
    /// That is deliberate: the alternative is every caller branching on tool count, and the branch
    /// that gets forgotten is the multi-tool one, because nobody here has such a printer.
    /// </para>
    /// <para>
    /// <b>Empty on a printer that has reported nothing at all</b>, which is the honest answer - not a
    /// synthesised tool with a null material, which would read as "one tool, empty".
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PrinterToolState>> ReadToolsAsync(int printerId,
                                                                     CancellationToken cancellationToken)
    {
        PrinterLiveState? live = await _dbContext.PrinterLiveStates
                                                 .AsNoTracking()
                                                 .Include(state => state.Slots)
                                                 .SingleOrDefaultAsync(state => state.PrinterId == printerId,
                                                                       cancellationToken);

        if (live is null)
        {
            return [];
        }

        // The nozzle facts come from INFO rather than telemetry, so they are a second read joined by
        // tool number. Worth the query: which head is hardened is the question an abrasive hold
        // raises, and the slot block does not carry it.
        Dictionary<int, PrinterTool> described = await _dbContext.PrinterTools
                                                                 .AsNoTracking()
                                                                 .Where(tool => tool.PrinterId == printerId)
                                                                 .ToDictionaryAsync(tool => tool.ToolNumber,
                                                                                    cancellationToken);

        if (live.Slots.Count > 0)
        {
            return live.Slots
                       .OrderBy(slot => slot.SlotNumber)
                       .Select(slot => new PrinterToolState(slot.SlotNumber,
                                                            slot.Material,
                                                            slot.Temperature,
                                                            live.ActiveSlot == slot.SlotNumber,
                                                            Nozzle(described, slot.SlotNumber),
                                                            IsHardened(described, slot.SlotNumber)))
                       .ToList();
        }

        // No slot block, so one tool - firmware sends the block only above one. The material still
        // goes through LoadedFilament: the flat field carries the "---" sentinel unstripped on rows
        // written before that fix, and an offline printer keeps whatever it last said.
        return
        [
            new PrinterToolState(1,
                                 LoadedFilament.Of(live.Material),
                                 live.NozzleTemperature,
                                 IsPicked: false,
                                 Nozzle(described, 1),
                                 IsHardened(described, 1)),
        ];
    }

    private static float? Nozzle(Dictionary<int, PrinterTool> described, int toolNumber)
    {
        return described.TryGetValue(toolNumber, out PrinterTool? tool) ? tool.NozzleDiameter : null;
    }

    private static bool IsHardened(Dictionary<int, PrinterTool> described, int toolNumber)
    {
        return described.TryGetValue(toolNumber, out PrinterTool? tool) && tool.Hardened;
    }

    /// <summary>Reads the situation for one printer.</summary>
    public async Task<ToolTarget> ReadAsync(int printerId, CancellationToken cancellationToken)
    {
        int? activeSlot = await _dbContext.PrinterLiveStates
                                          .AsNoTracking()
                                          .Where(state => state.PrinterId == printerId)
                                          .Select(state => state.ActiveSlot)
                                          .SingleOrDefaultAsync(cancellationToken);

        int toolCount = await _dbContext.PrinterTools
                                        .AsNoTracking()
                                        .CountAsync(tool => tool.PrinterId == printerId, cancellationToken);

        return ToolTarget.For(activeSlot, toolCount);
    }
}
