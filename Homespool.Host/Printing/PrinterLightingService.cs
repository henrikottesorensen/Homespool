using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Printing;

/// <summary>
/// Setting how bright a printer's own lighting is - the one place the page and the API both go, so
/// the range and the which-printers rule have one copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>No state rule, unlike preheating.</b> Firmware takes the setting in any state, mid-print
/// included, and nothing about a print depends on the light - so a printer that is busy is not a
/// reason to refuse.
/// </para>
/// <para>
/// <b>On the API as well as the page</b>, which heating deliberately is not: the worst a leaked token
/// does with this is switch a light off or on, and nothing about it persists beyond the next person
/// to press the button.
/// </para>
/// <para>
/// <b><see cref="Capability.ControlPrinter"/> is asked before anything about the printer is read</b>,
/// for the reason <c>PrinterPreheatService</c> gives: a caller it would refuse should hear "not
/// allowed", not whether the printer has a light.
/// </para>
/// </remarks>
public class PrinterLightingService
{
    private readonly PrinterCommandService _commands;
    private readonly PrinterAccessService _access;
    private readonly TelemetryDbContext _telemetry;

    public PrinterLightingService(PrinterCommandService commands,
                                  PrinterAccessService access,
                                  TelemetryDbContext telemetry)
    {
        _commands = commands;
        _access = access;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Sets the printer's lighting to <paramref name="intensity"/> percent, and returns once the
    /// printer has taken it.
    /// </summary>
    /// <exception cref="TeamAccessDeniedException">Caller lacks <see cref="Capability.ControlPrinter"/>.</exception>
    /// <exception cref="LightingIntensityOutOfRangeException">Not 0 to <see cref="PrinterLighting.MaxIntensity"/>.</exception>
    /// <exception cref="NoLightingException">The printer has no lighting that can be set.</exception>
    /// <exception cref="PrinterRefusedException">The printer answered, and said no.</exception>
    public async Task SetAsync(int printerId, Caller caller, int intensity, CancellationToken cancellationToken)
    {
        // The capability SetLighting declares, by inheriting the intent default.
        Printer printer = await _access.RequireAsync(printerId, caller, Capability.ControlPrinter, cancellationToken);

        if (intensity is < 0 or > PrinterLighting.MaxIntensity)
        {
            throw new LightingIntensityOutOfRangeException(intensity);
        }

        int? reported = await _telemetry.PrinterLiveStates
                                        .AsNoTracking()
                                        .Where(state => state.PrinterId == printerId)
                                        .Select(state => state.ChamberLedIntensity)
                                        .SingleOrDefaultAsync(cancellationToken);

        if (!PrinterLighting.Has(printer.Model, reported))
        {
            throw new NoLightingException(printerId);
        }

        CommandOutcome? answer = await _commands.SendCommandAsync(printerId, new SetLighting(intensity), caller, cancellationToken);

        if (answer is not null && answer.EventType is PrinterEventType.Rejected or PrinterEventType.Failed)
        {
            throw new PrinterRefusedException(answer.EventType, answer.Reason);
        }
    }
}
