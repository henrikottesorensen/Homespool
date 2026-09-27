using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles.GCode;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.DTO.EventMessages;
using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// The plate of the print a printer is running, asked of the printer once per print and remembered
/// until the next.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked of the printer rather than read from a file held here</b>, so it describes what the
/// printer is actually running - a print started at the panel as much as one sent from here. Two
/// questions: <c>SEND_JOB_INFO</c> for the running file's path, then <c>SEND_FILE_INFO</c> on that
/// path, whose answer carries the slicer's headers.
/// </para>
/// <para>
/// <b>Once per print, whoever is looking.</b> The file-info answer is about 90 KB and holds the
/// printer's one in-flight command slot for over a second, which the queue's own commands are
/// refused during. So one fetch serves every viewer - a singleton, keyed on the firmware's job id -
/// and a failed one is not repeated for <see cref="RetryAfter"/>, rather than on every poll of every
/// open page.
/// </para>
/// <para>
/// <b>The fetch runs in its own scope and is not cancelled by whoever started it.</b> Other viewers
/// may be awaiting the same task, and a page that navigates away must not take their answer with it.
/// It is bounded all the same, by the command response timeout on each of its two questions.
/// </para>
/// </remarks>
public sealed class PrinterPlateReader
{
    /// <summary>How long a failed fetch is remembered before a view may start another.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long one page render waits for a fetch before rendering without it.</summary>
    public static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PrinterPlateReader> _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Fetch> _fetches = [];

    public PrinterPlateReader(IServiceScopeFactory scopeFactory,
                              TimeProvider timeProvider,
                              ILogger<PrinterPlateReader> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The plate of firmware job <paramref name="jobId"/> on this printer, waiting at most
    /// <paramref name="wait"/> for it.
    /// </summary>
    /// <param name="printerId">The printer.</param>
    /// <param name="jobId">The job it reports running, from live state. A different id starts a new fetch.</param>
    /// <param name="caller">Who is looking. A fetch this starts is asked under their permission, which needs <see cref="Capability.ViewPrinter"/> only.</param>
    /// <param name="wait">
    /// How long to wait for a fetch still in flight - <see cref="RenderWait"/> for a poll, zero for a
    /// full page load, which should not stall on the printer when the poll will fill it in.
    /// </param>
    /// <param name="cancellationToken">The caller's own; it stops the wait, never the fetch.</param>
    public async Task<PlateReading> ReadAsync(int printerId,
                                              int jobId,
                                              Caller caller,
                                              TimeSpan wait,
                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        Task<PlateLayout?> fetch = Start(printerId, jobId, caller);

        try
        {
            PlateLayout? layout = await fetch.WaitAsync(wait, _timeProvider, cancellationToken);

            return new PlateReading(layout, Settled: true);
        }
        catch (TimeoutException)
        {
            return new PlateReading(null, Settled: false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The fetch failed, and has already said why. Rendered as not known yet: the next poll
            // after RetryAfter asks again.
            return new PlateReading(null, Settled: false);
        }
    }

    private Task<PlateLayout?> Start(int printerId, int jobId, Caller caller)
    {
        lock (_gate)
        {
            if (_fetches.TryGetValue(printerId, out Fetch? existing) &&
                existing.JobId == jobId &&
                (!existing.Task.IsFaulted || _timeProvider.GetUtcNow() - existing.StartedAt < RetryAfter))
            {
                return existing.Task;
            }

            Task<PlateLayout?> task = FetchAsync(printerId, jobId, caller);

            // Observed here, because the view that started it may have stopped waiting: a fault
            // nobody reads is reported as unobserved, and this one has already been logged.
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                                  TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

            _fetches[printerId] = new Fetch(jobId, task, _timeProvider.GetUtcNow());

            return task;
        }
    }

    private async Task<PlateLayout?> FetchAsync(int printerId, int jobId, Caller caller)
    {
        // Off the caller's stack before anything else, so the lock above is never held across the
        // first wire question.
        await Task.Yield();

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            PrinterCommandService commands = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

            CommandOutcome<JobInfoEventDataDTO>? job =
                await commands.AskAsync(printerId, new SendJobInfo { JobId = jobId }, caller, CancellationToken.None);

            if (job?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                throw new PrinterRefusedException(job.EventType, job.Reason);
            }

            if (job?.Answer?.Path is not { Length: > 0 } path)
            {
                // Settled rather than retried: a job the printer will not name a file for will not
                // start naming one.
                return null;
            }

            CommandOutcome<PlateInfoEventDataDTO>? file =
                await commands.AskAsync(printerId, new SendPlateInfo { Path = path }, caller, CancellationToken.None);

            if (file?.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
            {
                throw new PrinterRefusedException(file.EventType, file.Reason);
            }

            return PlateLayout.Parse(file?.Answer?.ObjectsInfo, file?.Answer?.BedShape);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "[{PrinterId}] could not read the plate of firmware job {JobId}", printerId, jobId);

            throw;
        }
    }

    /// <summary>One printer's fetch: which job it is for, and when it started.</summary>
    private sealed record Fetch(int JobId, Task<PlateLayout?> Task, DateTimeOffset StartedAt);
}

/// <summary>What <see cref="PrinterPlateReader.ReadAsync"/> found.</summary>
/// <param name="Layout">The plate, or null when there is none to draw.</param>
/// <param name="Settled">
/// Whether that null is an answer - the file carries no layout - or only means the answer has not
/// arrived yet. The page words the two differently.
/// </param>
public sealed record PlateReading(PlateLayout? Layout, bool Settled);
