using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using Homespool.Host.Authorisation;
using Homespool.Host.DTO;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Host.Services;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// What a printer has reported about itself, over the app API: its state now, and its temperatures
/// over time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own controller rather than more routes on <see cref="PrinterController"/></b>, which owns
/// things done <i>to</i> a printer and waits on its answer. Nothing here reaches the printer: both
/// reads are of what it last sent, so they answer as fast when it is switched off - which is exactly
/// why <see cref="PrinterTelemetryReadDTO.Connected"/> has to be read first.
/// </para>
/// <para>
/// <c>ViewPrinter</c> is the whole gate, as it is for the printer page that shows the same numbers.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1")]
[Authorize(Policy = Authorisation.Policies.Api)]

// 401 is the auth policy's, not any action's - an unauthenticated caller never reaches one.
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class PrinterTelemetryController : ControllerBase
{
    private readonly PrinterQueryService _printers;
    private readonly ToolTargetReader _tools;
    private readonly PrinterConnectionRegistry _connections;
    private readonly TimeProvider _timeProvider;
    private readonly UserManager<HSUser> _userManager;

    public PrinterTelemetryController(PrinterQueryService printers,
                                      ToolTargetReader tools,
                                      PrinterConnectionRegistry connections,
                                      TimeProvider timeProvider,
                                      UserManager<HSUser> userManager)
    {
        _printers = printers;
        _tools = tools;
        _connections = connections;
        _timeProvider = timeProvider;
        _userManager = userManager;
    }

    /// <summary>
    /// The printer's state as it last reported it, and every tool it has.
    /// <c>GET /api/v1/printers/{uuid}/telemetry</c>.
    /// </summary>
    [HttpGet]
    [Route("printers/{uuid:guid}/telemetry")]
    public async Task<Results<Ok<PrinterTelemetryReadDTO>, ForbiddenProblem, NotFoundProblem>> Get(
        Guid uuid,
        CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        PrinterWithState? printer =
            await _printers.GetPrinterWithStateForUserAsync(uuid, CallerResolver.For(user, User), cancellationToken);

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        IReadOnlyList<PrinterToolState> tools = await _tools.ReadToolsAsync(printer.Printer.Id, cancellationToken);

        return TypedResults.Ok(Read(printer.LiveState,
                                    _connections.IsConnected(printer.Printer.Id),
                                    tools));
    }

    /// <summary>
    /// Temperatures over a window, bucketed to a bounded number of points.
    /// <c>GET /api/v1/printers/{uuid}/telemetry/temperatures</c>.
    /// </summary>
    /// <param name="uuid">The printer.</param>
    /// <param name="from">
    /// The window's start. Without it the window is the running print's own length, as the printer
    /// page draws it - between 15 minutes and a day - or the last hour when nothing is printing.
    /// </param>
    /// <param name="to">The window's end. Now, without it.</param>
    /// <param name="cancellationToken">Aborted with the request.</param>
    /// <remarks>
    /// <b>A window longer than a day is cut to the day ending at <paramref name="to"/></b> rather than
    /// refused, as the queue clamps a position past its end. The bucketing keeps the answer small at
    /// any length; the cap bounds what one request makes the database scan.
    /// </remarks>
    [HttpGet]
    [Route("printers/{uuid:guid}/telemetry/temperatures")]
    public async Task<Results<Ok<TemperatureSeriesReadDTO>, BadRequestProblem, ForbiddenProblem, NotFoundProblem>> Temperatures(
        Guid uuid,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        HSUser? user = await _userManager.GetUserAsync(User);

        if (user is null)
        {
            return this.NoAccount();
        }

        Caller caller = CallerResolver.For(user, User);
        PrinterWithState? printer = await _printers.GetPrinterWithStateForUserAsync(uuid, caller, cancellationToken);

        if (printer is null)
        {
            return this.NotFoundProblem();
        }

        DateTimeOffset end = to ?? _timeProvider.GetUtcNow();

        // The default window is laid before the end, which subtracts up to a day. An end within a day
        // of the calendar's first instant leaves nothing to subtract from, and DateTimeOffset throws
        // rather than saturating. The day's cut below cannot underflow - it only moves a start that is
        // already before the end - so this is the one place a caller's value can.
        if (end < DateTimeOffset.MinValue + TemperatureWindow.Maximum)
        {
            return this.BadRequestProblem("The window has to end at least a day after 0001-01-01.");
        }

        // The page's window, moved to end where the caller asked: the running print's length, or an
        // hour when there is none.
        (DateTimeOffset pageFrom, DateTimeOffset pageTo) = TemperatureWindow.For(printer.LiveState, _timeProvider.GetUtcNow());
        DateTimeOffset start = from ?? end - (pageTo - pageFrom);

        if (start >= end)
        {
            return this.BadRequestProblem("The window has to start before it ends.");
        }

        if (end - start > TemperatureWindow.Maximum)
        {
            start = end - TemperatureWindow.Maximum;
        }

        TemperatureSeries? series = await _printers.GetTemperatureSeriesAsync(uuid, caller, start, end, cancellationToken);

        return series is null ?
            this.NotFoundProblem() :
            TypedResults.Ok(TemperatureSeriesReadDTO.FromSeries(series));
    }

    private static PrinterTelemetryReadDTO Read(PrinterLiveState? live,
                                                bool connected,
                                                IReadOnlyList<PrinterToolState> tools)
    {
        return new PrinterTelemetryReadDTO
        {
            Connected = connected,
            LastSeenAt = live?.LastSeenAt,
            State = (live?.Status ?? PrinterStatus.Unknown).ToConnectState(),
            Attention = live?.AttentionCode is null && live?.AttentionText is null ?
                null :
                new AttentionReadDTO { Code = live.AttentionCode, Text = live.AttentionText },

            // Firmware sends the job block only while it has a job, and the merger clears these fields
            // when the block stops arriving - so all of them absent is the printer saying "no job".
            // The filament odometer sits outside that block and says nothing about a job.
            Job = live is null || (live.JobId is null && live.Progress is null && live.TimePrinting is null &&
                                   live.TimeRemaining is null && live.TimeToFilamentChange is null) ?
                null :
                new JobProgressReadDTO
                {
                    Progress = live.Progress,
                    TimePrinting = live.TimePrinting,
                    TimeRemaining = live.TimeRemaining,
                    TimeToFilamentChange = live.TimeToFilamentChange,
                },
            Temperatures = new TemperaturesReadDTO
            {
                Nozzle = new HeaterReadDTO { Current = live?.NozzleTemperature, Target = live?.TargetNozzleTemperature },
                Bed = new HeaterReadDTO { Current = live?.BedTemperature, Target = live?.TargetBedTemperature },
                Chamber = new HeaterReadDTO { Current = live?.ChamberTemperature, Target = live?.ChamberTargetTemperature },
                Heatbreak = live?.HeatbreakTemperature,
                Enclosure = live?.EnclosureTemperature,
                Psu = live?.PsuTemperature,
                Ambient = live?.AmbientTemperature,
            },
            Speed = live?.Speed,
            Flow = live?.Flow,
            Fans = new FansReadDTO { Extruder = live?.ExtruderFan, Print = live?.PrintFan },
            FilamentUsed = live?.FilamentUsed,
            Tools = [.. tools.OrderBy(tool => tool.ToolNumber).Select(ToolReadDTO.FromState)],
        };
    }
}
