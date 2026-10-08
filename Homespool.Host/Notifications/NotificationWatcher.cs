using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Model;

namespace Homespool.Host.Notifications;

/// <summary>
/// Notices prints ending and queues becoming held, by reading what the queue has committed, notices a
/// printer going quiet in the middle of a print, and releases the waits <see cref="AttentionWatch"/>
/// has let settle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the database rather than told by the queue.</b> A print row is closed at seven
/// places in <c>QueueAdvancer</c> and a hold is set at five; being told would mean a line at each,
/// and the next place somebody writes would be the one nobody announced. Reading the committed rows
/// also means a change rolled back is never announced, and nothing here can slow the queue down.
/// </para>
/// <para>
/// <b>A hold is announced once, as a hold.</b> Each one leaves a failed row in history in the same
/// save, marked with <c>PrintJob.HoldReason</c>; that row's end is not announced, or the person who
/// queued the file would hear about one event twice.
/// </para>
/// <para>
/// <b>A print is announced by its end time, a hold by its appearance.</b> <c>EndedAt</c> is written
/// once, so a watermark on it finds each end exactly once. A hold has no such moment -
/// <c>BlockedAt</c> is when it was last confirmed, rewritten while it lasts - so holds are compared
/// against the set held at the previous look, and a hold is news when its row was not held then, or
/// was held for a different reason.
/// </para>
/// <para>
/// <b>A printer is lost when it has been disconnected for <see cref="LostAfter"/> with a print
/// open</b>, and not before: Wi-Fi drops and a printer reconnects within seconds, and a phone that
/// buzzed at every blip would be turned off. Asked of the connection registry rather than of the
/// live state, because nothing writes a disconnect there - a printer unplugged mid-print goes on
/// saying <c>Printing</c>. Once per print, however often it comes and goes.
/// </para>
/// <para>
/// <b>A start announces nothing old.</b> The watermark begins at the start and the first look at
/// the holds only learns them, so a restart does not replay the prints that ended while the service
/// was down or the holds that were already there.
/// </para>
/// </remarks>
public sealed class NotificationWatcher : BackgroundService
{
    /// <summary>How often to look. A print's end is not urgent to the second; a phone buzzing a few
    /// seconds after the printer beeps is soon enough, and the queries are two small reads.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How far behind the newest end the next look reaches, so a row committed with an end time a
    /// moment older than one already seen is not stepped over. Ends already announced are remembered
    /// across this window, not announced twice.
    /// </summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(1);

    private static readonly PrintState[] Announced = [PrintState.Finished, PrintState.Stopped, PrintState.Failed];

    /// <summary>How long a printer with a print open must stay disconnected to be announced as lost.</summary>
    public static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly Printing.PrinterConnectionRegistry _connections;
    private readonly AttentionWatch _attention;
    private readonly NotificationQueue _queue;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationWatcher> _logger;

    // One look at a time: the timer's and a test's may meet, and what a look remembers is not safe to
    // share between two of them.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<long, DateTimeOffset> _announcedEnds = [];

    // Per open print: since when its printer has been unreachable, and whether that was announced.
    private readonly Dictionary<long, DateTimeOffset> _quietSince = [];
    private readonly HashSet<long> _announcedLost = [];

    private Dictionary<long, PrintHoldReason>? _held;
    private DateTimeOffset _since;

    public NotificationWatcher(IServiceScopeFactory scopes,
                               Printing.PrinterConnectionRegistry connections,
                               AttentionWatch attention,
                               NotificationQueue queue,
                               TimeProvider time,
                               ILogger<NotificationWatcher> logger)
    {
        _scopes = scopes;
        _connections = connections;
        _attention = attention;
        _queue = queue;
        _time = time;
        _logger = logger;

        // To the millisecond, because that is all the database keeps. A watermark finer than the
        // column would sit a fraction after an end committed in the same millisecond: the query,
        // whose parameter is truncated too, would still find the row, and the pruning below would
        // then forget it and let the next look announce it again.
        DateTimeOffset now = time.GetUtcNow();
        _since = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    /// <summary>One look. Public so a test can drive it without waiting for the timer.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            foreach (PrinterNeedsAttention waiting in _attention.Due(_time.GetUtcNow()))
            {
                _queue.Publish(waiting);
            }

            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            HomespoolDbContext db = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            await LookForEndsAsync(db, cancellationToken);
            await LookForHoldsAsync(db, cancellationToken);
            await LookForLostPrintersAsync(db, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await LookAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // The next look tries again, which is the right answer to a busy database; and a
                    // watcher that died would be notifications that stopped without a word.
                    _logger.LogError(e, "Looking for things to notify about failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task LookForEndsAsync(HomespoolDbContext db, CancellationToken cancellationToken)
    {
        DateTimeOffset since = _since;

        var ended = await db.PrintJobs
                            .AsNoTracking()
                            .Where(job => job.EndedAt != null && job.EndedAt >= since)
                            .Select(job => new { job.Id, job.PrinterId, job.State, job.HoldReason, EndedAt = job.EndedAt!.Value })
                            .ToListAsync(cancellationToken);

        foreach (var job in ended)
        {
            if (!_announcedEnds.TryAdd(job.Id, job.EndedAt))
            {
                continue;
            }

            // Unknown is a row the loop closed because it could not find out - announcing a guess as
            // news would be worse than silence, and the history page already says so. A hold's own
            // record is announced as the hold, by LookForHoldsAsync; this would say it a second time.
            if (Announced.Contains(job.State) && job.HoldReason is null)
            {
                _queue.Publish(new PrintEnded(job.PrinterId, job.Id));
            }
        }

        if (ended.Count > 0)
        {
            DateTimeOffset newest = ended.Max(job => job.EndedAt);
            _since = newest - Overlap > _since ? newest - Overlap : _since;
        }

        foreach (long forgotten in _announcedEnds.Where(entry => entry.Value < _since).Select(entry => entry.Key).ToList())
        {
            _announcedEnds.Remove(forgotten);
        }
    }

    private async Task LookForLostPrintersAsync(HomespoolDbContext db, CancellationToken cancellationToken)
    {
        var open = await db.PrintJobs
                           .AsNoTracking()
                           .Where(job => job.EndedAt == null)
                           .Select(job => new { job.Id, job.PrinterId })
                           .ToListAsync(cancellationToken);

        DateTimeOffset now = _time.GetUtcNow();
        HashSet<long> stillOpen = [.. open.Select(job => job.Id)];

        foreach (var job in open)
        {
            if (_connections.IsConnected(job.PrinterId))
            {
                _quietSince.Remove(job.Id);

                continue;
            }

            if (!_quietSince.TryGetValue(job.Id, out DateTimeOffset since))
            {
                _quietSince[job.Id] = now;

                continue;
            }

            if (now - since >= LostAfter && _announcedLost.Add(job.Id))
            {
                _queue.Publish(new PrinterLost(job.PrinterId, job.Id));
            }
        }

        // A print that has closed needs no more watching, and its id will not come back.
        _quietSince.Keys.Where(id => !stillOpen.Contains(id)).ToList().ForEach(id => _quietSince.Remove(id));
        _announcedLost.RemoveWhere(id => !stillOpen.Contains(id));
    }

    private async Task LookForHoldsAsync(HomespoolDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.FilesOnPrinters
                           .AsNoTracking()
                           .Where(row => row.HoldReason != null)
                           .Select(row => new { row.Id, row.PrinterId, Reason = row.HoldReason!.Value })
                           .ToListAsync(cancellationToken);

        Dictionary<long, PrintHoldReason> held = rows.ToDictionary(row => row.Id, row => row.Reason);

        if (_held is Dictionary<long, PrintHoldReason> before)
        {
            foreach (var row in rows)
            {
                if (!before.TryGetValue(row.Id, out PrintHoldReason was) || was != row.Reason)
                {
                    _queue.Publish(new QueueHeld(row.PrinterId, row.Reason));
                }
            }
        }

        _held = held;
    }
}
