using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Services;

/// <summary>
/// Team-permission-checked reads and edits of <see cref="Printer"/>, for the app-facing
/// <c>GET/PATCH /api/v1/printers</c> surface (phase-1.5 §15 step 7b). Kept separate from
/// <see cref="PrusaConnect.PrusaConnectService"/>, which owns the registration protocol and the
/// claim that creates a printer - this owns everything that happens to one afterwards.
/// </summary>
public class PrinterQueryService
{
    /// <summary>Rows shown on the printer detail page - a display choice, not a protocol knob, so
    /// fixed constants rather than a config option.</summary>
    private const int RecentSampleCount = 50;

    private const int RecentEventCount = 20;

    /// <summary>
    /// How many points the temperature graph is drawn from, whatever the window's length.
    /// </summary>
    /// <remarks>
    /// Chosen against the drawing rather than the data: the graph is ~720 units wide, so this puts a
    /// point every four units - finer than a line stroke, and past the point where more of them
    /// change the picture.
    /// </remarks>
    private const int TargetPointCount = 180;

    private readonly HomespoolDbContext _dbContext;
    private readonly PrinterAccessService _access;

    /// <summary>
    /// Which teams the caller may see printers on. <b>Listing cannot use
    /// <see cref="PrinterAccessService"/></b>, which answers about one printer at a time; and the
    /// capability column cannot be matched in SQL without a <c>LIKE</c> that is neither indexable nor
    /// exact. See <see cref="TeamCapabilityLookup"/>.
    /// </summary>
    private readonly TeamCapabilityLookup _teams;
    private readonly TimeProvider _timeProvider;

    public PrinterQueryService(HomespoolDbContext dbContext,
                               PrinterAccessService access,
                               TeamCapabilityLookup teams,
                               TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _access = access;
        _teams = teams;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Every printer belonging to a team the caller may view, oldest first. Read-only,
    /// so untracked.
    /// </summary>
    public async Task<IReadOnlyList<Printer>> ListPrintersForUserAsync(Caller caller, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> teams = await _teams.TeamsAllowingAsync(caller, Capability.ViewPrinter, cancellationToken);

        return await _dbContext.Printers
                               .AsNoTracking()
                               .Where(p => teams.Contains(p.TeamId))
                               .OrderBy(p => p.Id)
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// As <see cref="ListPrintersForUserAsync"/>, but pairs each printer with its
    /// <see cref="PrinterLiveState"/> - which is where a printer's <em>actual</em> status lives.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Printer.Status"/> is not a live value and never has been.</b> It is written once,
    /// as <see cref="PrinterStatus.Unknown"/>, when the printer row is created, and nothing updates it
    /// afterwards; telemetry writes <see cref="PrinterLiveState.Status"/> instead, on a different
    /// entity. Anything reporting a printer's state to a caller has to join to that, which is what
    /// this exists for. Null live state means the printer has never connected.
    /// </remarks>
    public async Task<IReadOnlyList<PrinterWithState>> ListPrintersWithStateForUserAsync(
        Caller caller,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> teams = await _teams.TeamsAllowingAsync(caller, Capability.ViewPrinter, cancellationToken);

        return await _dbContext.Printers
                               .AsNoTracking()
                               .Where(p => teams.Contains(p.TeamId))
                               .OrderBy(p => p.Id)
                               .Select(p => new PrinterWithState(
                                           p,
                                           _dbContext.PrinterLiveStates.SingleOrDefault(s => s.PrinterId == p.Id),
                                           _dbContext.TeamMembers.SingleOrDefault(m => m.TeamId == p.TeamId && m.UserId == caller.UserId),
                                           _dbContext.Teams.SingleOrDefault(t => t.Id == p.TeamId)))
                               .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// As <see cref="GetPrinterForUserAsync"/>, paired with its <see cref="PrinterLiveState"/>. See
    /// <see cref="ListPrintersWithStateForUserAsync"/> for why the join is necessary rather than
    /// convenient.
    /// </summary>
    public async Task<PrinterWithState?> GetPrinterWithStateForUserAsync(Guid uuid, Caller caller, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> teams = await _teams.TeamsAllowingAsync(caller, Capability.ViewPrinter, cancellationToken);

        return await _dbContext.Printers
                         .AsNoTracking()
                         .Where(p => p.Uuid == uuid && teams.Contains(p.TeamId))
                         .Select(p => new PrinterWithState(
                                     p,
                                     _dbContext.PrinterLiveStates.SingleOrDefault(s => s.PrinterId == p.Id),
                                     _dbContext.TeamMembers.SingleOrDefault(m => m.TeamId == p.TeamId && m.UserId == caller.UserId),
                                     _dbContext.Teams.SingleOrDefault(t => t.Id == p.TeamId)))
                         .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// A single printer by its public <see cref="Printer.Uuid"/>, or <c>null</c> if it doesn't
    /// exist <b>or</b> the caller lacks <c>CanRead</c> on its team - the two cases are
    /// indistinguishable on purpose, so a 404 never confirms a UUID belongs to someone else's team.
    /// </summary>
    /// <remarks>
    /// Returns the printer alone, which is all a caller resolving one for a permission check needs -
    /// <c>PrinterController</c> is the example. A caller that reports state to a user wants
    /// <see cref="GetPrinterWithStateForUserAsync"/> instead.
    /// </remarks>
    public Task<Printer?> GetPrinterForUserAsync(Guid uuid, Caller caller, CancellationToken cancellationToken)
    {
        return _access.FindAsync(uuid, caller, Capability.ViewPrinter, cancellationToken);
    }

    /// <summary>
    /// Updates a printer's user-editable fields - name and location only; moving it between teams
    /// is deferred (needs a permission check on both the old and new team). Returns <c>null</c> under
    /// the same "doesn't exist or caller can't even read it" rule as <see cref="GetPrinterForUserAsync"/>.
    /// A caller who can read but not manage the printer's team gets <see cref="TeamAccessDeniedException"/>
    /// instead - safe to distinguish, since reaching that branch already proves they can see the printer.
    /// </summary>
    /// <remarks>
    /// Returns the live state alongside, so the response to an edit describes the same resource the
    /// next <c>GET</c> will - a PATCH answering <c>UNKNOWN</c> while a GET one second later says
    /// <c>PRINTING</c> would look like the edit had reset something.
    /// </remarks>
    /// <summary>
    /// Sets whether this printer may be marked ready from its page - see
    /// <see cref="Printer.RemoteReadyAllowed"/> for what the flag asserts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A method of its own rather than another parameter on <see cref="UpdatePrinterAsync"/></b>,
    /// which serves <c>PATCH /api/v1/printers</c>: name and location are labels, and this is a
    /// standing judgement about whether a safety prompt can be answered honestly from a screen.
    /// Folding it in would put it on that surface as one more editable field, and the two want
    /// different callers.
    /// </para>
    /// <para>
    /// Same two refusal shapes as its neighbour, in the same order and for the same reason: a caller
    /// who cannot read the printer gets <c>null</c>, because naming a refusal would confirm the UUID
    /// exists, and one who can read but not manage has already been shown it.
    /// </para>
    /// </remarks>
    public async Task<bool?> SetRemoteReadyAllowedAsync(Guid uuid,
                                                        Caller caller,
                                                        bool allowed,
                                                        CancellationToken cancellationToken)
    {
        if (await _access.FindAsync(uuid, caller, Capability.ViewPrinter, cancellationToken) is null)
        {
            return null;
        }

        Printer printer = await _dbContext.Printers.SingleAsync(p => p.Uuid == uuid, cancellationToken);

        await _access.RequireAsync(printer.Id, caller, Capability.ManagePrinter, cancellationToken);

        printer.RemoteReadyAllowed = allowed;
        printer.UpdatedAt = _timeProvider.GetUtcNow();

        await _dbContext.SaveChangesAsync(cancellationToken);

        return allowed;
    }

    public async Task<PrinterWithState?> UpdatePrinterAsync(Guid uuid,
                                                            Caller caller,
                                                            string? name,
                                                            string? location,
                                                            CancellationToken cancellationToken)
    {
        // Two questions, two refusal shapes, and the order matters: a caller who cannot even read
        // this printer gets null, because saying "forbidden" would confirm the UUID exists. One who
        // can read but not manage has already been shown it, so naming the refusal is safe.
        if (await _access.FindAsync(uuid, caller, Capability.ViewPrinter, cancellationToken) is null)
        {
            return null;
        }

        // Tracked, unlike the gate's copy - this one is about to be edited and saved.
        Printer printer = await _dbContext.Printers.SingleAsync(p => p.Uuid == uuid, cancellationToken);

        await _access.RequireAsync(printer.Id, caller, Capability.ManagePrinter, cancellationToken);

        printer.Name = name;
        printer.Location = location;
        printer.UpdatedAt = _timeProvider.GetUtcNow();

        await _dbContext.SaveChangesAsync(cancellationToken);

        PrinterLiveState? liveState = await _dbContext.PrinterLiveStates
                                                      .AsNoTracking()
                                                      .SingleOrDefaultAsync(s => s.PrinterId == printer.Id, cancellationToken);

        Team? team = await _dbContext.Teams
                                     .AsNoTracking()
                                     .SingleOrDefaultAsync(t => t.Id == printer.TeamId, cancellationToken);

        // The DTO carries the caller's own permissions, so the membership is data here rather than a
        // check - the checks above have already run.
        TeamMember? membership = await _dbContext.TeamMembers
                                                 .AsNoTracking()
                                                 .SingleOrDefaultAsync(m => m.TeamId == printer.TeamId && m.UserId == caller.UserId,
                                                                       cancellationToken);

        return new PrinterWithState(printer, liveState, membership, team);
    }

    /// <summary>
    /// A printer's live state plus recent telemetry/event history, for the detail page. Same
    /// "doesn't exist or caller can't even read it" rule as <see cref="GetPrinterForUserAsync"/> -
    /// both cases return <c>null</c> so a 404 never confirms a UUID belongs to someone else's team.
    /// </summary>
    public async Task<PrinterStatistics?> GetPrinterStatisticsForUserAsync(Guid uuid,
                                                                           Caller caller,
                                                                           CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> teams = await _teams.TeamsAllowingAsync(caller, Capability.ViewPrinter, cancellationToken);

        Printer? printer = await _dbContext.Printers
                                           .AsNoTracking()
                                           .Include(p => p.Team)
                                           .SingleOrDefaultAsync(p => p.Uuid == uuid && teams.Contains(p.TeamId),
                                                                 cancellationToken);

        if (printer is null)
        {
            return null;
        }

        PrinterLiveState? liveState = await _dbContext.PrinterLiveStates
                                                      .AsNoTracking()
                                                      .SingleOrDefaultAsync(s => s.PrinterId == printer.Id, cancellationToken);

        List<TelemetrySample> samples = await _dbContext.TelemetrySamples
                                                        .AsNoTracking()
                                                        .Where(s => s.PrinterId == printer.Id)
                                                        .OrderByDescending(s => s.Timestamp)
                                                        .Take(RecentSampleCount)
                                                        .ToListAsync(cancellationToken);

        List<PrinterEvent> events = await _dbContext.PrinterEvents
                                                    .AsNoTracking()
                                                    .Where(e => e.PrinterId == printer.Id)
                                                    .OrderByDescending(e => e.Timestamp)
                                                    .Take(RecentEventCount)
                                                    .ToListAsync(cancellationToken);

        return new PrinterStatistics(printer, liveState, samples, events);
    }

    /// <summary>
    /// Nozzle and bed temperatures over a window, bucketed for the detail page's graph. Same
    /// "doesn't exist or the caller can't read it" rule as everything else here - both answer null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bucketing happens in SQLite, and that is the reason this is raw SQL.</b> The window can
    /// be a whole day, which at the default of storing every message is ~86 000 rows for one printer;
    /// this page refreshes its graph while somebody watches it. Grouping in the database returns
    /// <see cref="TargetPointCount"/> rows however long the print is, so what crosses into memory is
    /// bounded by the graph rather than by the print.
    /// </para>
    /// <para>
    /// <b>It works because <c>Timestamp</c> is stored as epoch milliseconds in an INTEGER column</b> -
    /// see <c>DateTimeOffsetToUnixMillisecondsConverter</c>. Integer division by the bucket width is
    /// the whole grouping key, and the index on <c>(PrinterId, Timestamp)</c> makes the range a
    /// contiguous scan. Against a TEXT date column none of that would hold.
    /// </para>
    /// <para>
    /// The two aggregates differ deliberately - see <see cref="TemperaturePoint"/> for why a measured
    /// temperature is averaged and a setpoint is not.
    /// </para>
    /// </remarks>
    public async Task<TemperatureSeries?> GetTemperatureSeriesAsync(Guid uuid,
                                                                    Caller caller,
                                                                    DateTimeOffset from,
                                                                    DateTimeOffset to,
                                                                    CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> teams = await _teams.TeamsAllowingAsync(caller, Capability.ViewPrinter, cancellationToken);

        int printerId = await _dbContext.Printers
                                        .AsNoTracking()
                                        .Where(p => p.Uuid == uuid && teams.Contains(p.TeamId))
                                        .Select(p => p.Id)
                                        .SingleOrDefaultAsync(cancellationToken);

        if (printerId == 0)
        {
            return null;
        }

        long fromMs = from.ToUnixTimeMilliseconds();
        long toMs = to.ToUnixTimeMilliseconds();

        // At least a second, so a short window does not ask for buckets finer than the stream that
        // fills them - the printer reports about once a second, and a 200 ms bucket would just draw
        // four empty points for every real one.
        long bucketMs = Math.Max(1000, (toMs - fromMs) / TargetPointCount);

        List<TemperatureBucketRow> rows = await _dbContext.Database
            .SqlQuery<TemperatureBucketRow>(
                $"""
                 SELECT MIN("Timestamp") AS "BucketStartMs",
                        AVG("NozzleTemperature") AS "Nozzle",
                        AVG("BedTemperature") AS "Bed",
                        MAX("TargetNozzleTemperature") AS "TargetNozzle",
                        MAX("TargetBedTemperature") AS "TargetBed"
                 FROM "TelemetrySamples"
                 WHERE "PrinterId" = {printerId}
                   AND "Timestamp" >= {fromMs}
                   AND "Timestamp" <= {toMs}
                 GROUP BY "Timestamp" / {bucketMs}
                 ORDER BY "BucketStartMs"
                 """)
            .ToListAsync(cancellationToken);

        return new TemperatureSeries(
            from,
            to,
            rows.Select(row => new TemperaturePoint(DateTimeOffset.FromUnixTimeMilliseconds(row.BucketStartMs),
                                                    row.Nozzle,
                                                    row.Bed,
                                                    row.TargetNozzle,
                                                    row.TargetBed))
                .ToList());
    }

    /// <summary>
    /// One row of <see cref="GetTemperatureSeriesAsync"/>'s aggregate, named for its columns.
    /// </summary>
    /// <remarks>
    /// Unmapped: EF matches these by name against the projection and nothing registers the type on
    /// the context, so it adds no entity, no snapshot entry and no migration. <c>double?</c>
    /// throughout because SQLite's <c>AVG</c> returns REAL whatever the column was, and null when the
    /// bucket held no reading at all.
    /// </remarks>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "EF materialises this by name from the raw query above; nothing in this "
                                     + "assembly constructs one, which is exactly what the rule looks for.")]
    private sealed class TemperatureBucketRow
    {
        public long BucketStartMs { get; set; }

        public double? Nozzle { get; set; }

        public double? Bed { get; set; }

        public double? TargetNozzle { get; set; }

        public double? TargetBed { get; set; }
    }
}

/// <summary>A printer paired with its last-known state, which may be absent if it has never
/// connected. See <see cref="PrinterQueryService.ListPrintersWithStateForUserAsync"/>.</summary>
/// <remarks>
/// <see cref="Membership"/> is the <em>calling user's</em> row, not the printer's - it is what makes
/// the <c>canRead</c>/<c>canUse</c>/<c>canManage</c> flags answerable, and it is why this record is
/// per-request rather than per-printer. Null only where a caller mapped a printer without asking on
/// whose behalf, which the queries here never do.
/// </remarks>
public sealed record PrinterWithState(
    Printer Printer,
    PrinterLiveState? LiveState,
    TeamMember? Membership = null,
    Team? Team = null);

/// <summary>Result of <see cref="PrinterQueryService.GetPrinterStatisticsForUserAsync"/> - a
/// printer's live state plus recent history, newest first.</summary>
public sealed record PrinterStatistics(
    Printer Printer,
    PrinterLiveState? LiveState,
    IReadOnlyList<TelemetrySample> RecentSamples,
    IReadOnlyList<PrinterEvent> RecentEvents);
