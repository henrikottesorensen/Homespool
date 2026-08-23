using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;

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
