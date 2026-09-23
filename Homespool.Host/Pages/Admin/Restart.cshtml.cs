using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Host.Authorisation;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Pages.Admin;

/// <summary>
/// Restarts the service, after saying what a restart would interrupt.
/// </summary>
/// <remarks>
/// <para>
/// <b>It warns and does not refuse.</b> Somebody pressing this has usually just saved a setting that
/// waits for a restart, and a transfer they are warned of here is one they can choose to wait for.
/// </para>
/// <para>
/// <b>Printers by name, across every team.</b> An administrator manages every account on the
/// deployment, so the names of its printers are not news to them - and "which printer" is what decides
/// whether to wait.
/// </para>
/// <para>
/// <b>Absent rather than refused outside a container</b>, because there nothing would start the
/// service again - see <see cref="ApplicationRestarter"/>.
/// </para>
/// </remarks>
[Authorize(Policy = Policies.Administrator)]
[RequireRecentProof]
public class RestartModel : PageModel
{
    /// <summary>
    /// A print on the machine, whether or not it is moving. A paused print is still one a person would
    /// not want told it had been interrupted.
    /// </summary>
    private static readonly PrinterStatus[] PrintingStatuses = [PrinterStatus.Printing, PrinterStatus.Paused];

    private readonly ApplicationRestarter _restarter;
    private readonly TransferOfferStore _offers;
    private readonly HomespoolDbContext _dbContext;
    private readonly TelemetryDbContext _telemetry;
    private readonly TelemetryStorageMode _telemetryMode;
    private readonly PrinterConnectionRegistry _registry;
    private readonly UserManager<HSUser> _users;

    /// <summary>Creates the page model.</summary>
    /// <param name="restarter">Does the restarting, and says whether it can.</param>
    /// <param name="offers">The transfers a restart would break.</param>
    /// <param name="dbContext">The printers' names.</param>
    /// <param name="telemetry">Which printers say they are printing.</param>
    /// <param name="telemetryMode">Whether a restart discards recorded telemetry.</param>
    /// <param name="registry">Which printers are still connected to say so.</param>
    /// <param name="users">Who is asking, for the log.</param>
    public RestartModel(ApplicationRestarter restarter,
                        TransferOfferStore offers,
                        HomespoolDbContext dbContext,
                        TelemetryDbContext telemetry,
                        TelemetryStorageMode telemetryMode,
                        PrinterConnectionRegistry registry,
                        UserManager<HSUser> users)
    {
        _restarter = restarter;
        _offers = offers;
        _dbContext = dbContext;
        _telemetry = telemetry;
        _telemetryMode = telemetryMode;
        _registry = registry;
        _users = users;
    }

    /// <summary>
    /// File transfers a restart would break, by file and printer. The printer's name is null when it
    /// has been removed since the file was offered.
    /// </summary>
    public IReadOnlyList<(string fileName, string? printerName)> Transfers { get; private set; } = [];

    /// <summary>Connected printers with a print on them, which carries on through a restart.</summary>
    public IReadOnlyList<string> Printing { get; private set; } = [];

    /// <summary>Whether this process holds telemetry in memory, so a restart discards it.</summary>
    public bool TelemetryInMemory { get; private set; }

    /// <summary>Whether the restart has been asked for, and the page is waiting for it.</summary>
    public bool Restarting { get; private set; }

    /// <summary>Says what a restart would interrupt now.</summary>
    /// <param name="cancellationToken">Abandons the read if the request goes away.</param>
    /// <returns>The page, or not found where nothing would start the service again.</returns>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!_restarter.IsAvailable)
        {
            return NotFound();
        }

        IReadOnlyList<(int printerId, string fileName)> offers = _offers.StandingOffers();

        List<int> printing = await _telemetry.PrinterLiveStates
                                             .AsNoTracking()
                                             .Where(state => PrintingStatuses.Contains(state.Status))
                                             .Select(state => state.PrinterId)
                                             .ToListAsync(cancellationToken);

        // Last-known state outlives the connection, so a printer switched off mid-print still reads
        // as printing. Only a printer that is here can be interrupted.
        printing = printing.Where(_registry.IsConnected).ToList();

        List<int> named = [.. offers.Select(offer => offer.printerId), .. printing];

        Dictionary<int, string> names = await _dbContext.Printers
                                                        .AsNoTracking()
                                                        .Where(printer => named.Contains(printer.Id))
                                                        .ToDictionaryAsync(printer => printer.Id,
                                                                           printer => printer.Name ??
                                                                                      printer.Model ??
                                                                                      printer.Uuid.ToString(),
                                                                           cancellationToken);

        Transfers = offers.Select(offer => (fileName: offer.fileName, printerName: names.GetValueOrDefault(offer.printerId)))
                          .OrderBy(transfer => transfer.printerName, StringComparer.CurrentCultureIgnoreCase)
                          .ThenBy(transfer => transfer.fileName, StringComparer.CurrentCultureIgnoreCase)
                          .ToList();

        Printing = printing.Where(names.ContainsKey)
                           .Select(printerId => names[printerId])
                           .Order(StringComparer.CurrentCultureIgnoreCase)
                           .ToList();

        TelemetryInMemory = _telemetryMode.InMemory;

        return Page();
    }

    /// <summary>Restarts the service once this response has been sent.</summary>
    /// <returns>The page, saying it is restarting.</returns>
    public async Task<IActionResult> OnPostAsync()
    {
        if (!_restarter.IsAvailable)
        {
            return NotFound();
        }

        HSUser? administrator = await _users.GetUserAsync(User);

        if (administrator is null)
        {
            return NotFound();
        }

        long administratorId = administrator.Id;

        // After the response rather than now: stopping drains requests in flight, but the page that
        // says what is happening, and waits for the service to come back, has to reach the browser
        // first.
        Response.OnCompleted(() =>
        {
            _restarter.Restart(administratorId);

            return Task.CompletedTask;
        });

        Restarting = true;

        return Page();
    }
}
