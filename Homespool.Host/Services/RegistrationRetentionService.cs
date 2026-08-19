using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Data;

namespace Homespool.Host.Services;

/// <summary>
/// Deletes pending registrations whose code has expired.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing else ever removed one.</b> A registration is deleted when a printer collects the token
/// its claim produced (<c>PrusaConnectService.GetToken</c>) — so a row nobody ever claims stays for
/// the life of the deployment. <c>TemporaryCodeExpiry</c> stops it being <i>usable</i>; until now
/// nothing stopped it being <i>stored</i>, and <c>POST /p/register</c> is anonymous, so anyone who
/// can reach the printer listener could mint rows at the rate limiter's ceiling.
/// </para>
/// <para>
/// <b>A sweep rather than a delete on the write path.</b> Sweeping inside
/// <c>GetPrinterCode</c> — the way <c>TransferOfferStore</c> sweeps as it offers — would put the cost
/// in a printer's own registration request, and there is no index on the expiry, so it is a scan.
/// Cheap on a healthy table and precisely not cheap on one somebody has been growing, which is the
/// case the sweep exists for.
/// </para>
/// <para>
/// <b>Expiry alone decides, with no grace period.</b> An expired row is already refused by every
/// lookup — both <c>GetToken</c> and <c>ClaimPrinterAsync</c> filter on
/// <c>TemporaryCodeExpiry &gt; now</c> — so deleting it changes nothing a caller could observe. A
/// printer polling a code that has just expired is told to register again either way.
/// </para>
/// <para>
/// Modelled on <see cref="TelemetryRetentionService"/>: hourly, its own scope per pass because a
/// <see cref="HomespoolDbContext"/> must not outlive one, and failures logged rather than thrown so
/// one bad pass does not end the service.
/// </para>
/// </remarks>
public sealed class RegistrationRetentionService : BackgroundService
{
    /// <summary>
    /// How often to sweep. Hourly, matching <see cref="TelemetryRetentionService"/> — a registration
    /// code lives 30 minutes by default, so an expired row is never around for long.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RegistrationRetentionService> _logger;

    public RegistrationRetentionService(IServiceScopeFactory scopeFactory,
                                        ILogger<RegistrationRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SweepInterval);

        try
        {
            do
            {
                await SweepAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - stoppingToken fired while awaiting the timer.
        }
    }

    /// <summary>
    /// One pass, exposed so a test can drive it deterministically.
    /// </summary>
    /// <remarks>
    /// Public for the reason <c>QueueAdvancer</c> and <c>TelemetryWriter</c> are: on .NET 10
    /// <see cref="BackgroundService.StartAsync"/> schedules <c>ExecuteAsync</c> onto the pool and
    /// returns, so starting and stopping a hosted service proves nothing about whether its first pass
    /// ran — see <c>notes/net10-breaking-changes.md</c>, where that race cost a day of chasing a
    /// telemetry flake. A test that wants a sweep calls this.
    /// </remarks>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            DateTimeOffset now = DateTimeOffset.UtcNow;

            int deleted = await context.PrusaConnectRegistrations
                                       .Where(registration => registration.TemporaryCodeExpiry <= now)
                                       .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation("Swept {Deleted} expired printer registration(s).", deleted);
            }
        }
        catch (Exception exception) when (exception is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A busy database is the ordinary case here, and the next pass is an hour away - which is
            // soon enough for rows that are already refused by every lookup.
            _logger.LogWarning(exception, "Could not sweep expired printer registrations; will retry on the next pass.");
        }
    }
}
