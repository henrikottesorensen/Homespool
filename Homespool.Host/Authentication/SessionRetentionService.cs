using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Authentication;

/// <summary>
/// Deletes session rows that no longer sign anybody in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing else removes a session nobody ends.</b> Signing out deletes the row, and so does a
/// request that finds its session dead - but a browser that is simply closed, or never comes back
/// after a password change elsewhere, leaves its row for the life of the deployment. Liveness is what
/// stops such a row being <i>usable</i>; this is what stops it being <i>stored</i>.
/// </para>
/// <para>
/// <b>Liveness alone decides, with no grace period</b>, and it is <see cref="UserSessionService"/>'s
/// definition rather than a second one here, so a sweep cannot delete a row a request would have
/// accepted.
/// </para>
/// <para>
/// Modelled on <c>RegistrationRetentionService</c>: hourly, its own scope per pass, and failures logged
/// rather than thrown so one bad pass does not end the service.
/// </para>
/// </remarks>
public sealed class SessionRetentionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionRetentionService> _logger;

    public SessionRetentionService(IServiceScopeFactory scopeFactory,
                                   ILogger<SessionRetentionService> logger)
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
    /// One pass, exposed so a test can drive it deterministically: on .NET 10 starting a hosted service
    /// proves nothing about whether its first pass ran.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();

            int deleted = await scope.ServiceProvider
                                     .GetRequiredService<UserSessionService>()
                                     .SweepAsync(cancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation("Swept {Deleted} ended session(s).", deleted);
            }
        }
        catch (Exception exception) when (exception is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A busy database is the ordinary case here, and the next pass is an hour away - which is
            // soon enough for rows that are already refused by every request.
            _logger.LogWarning(exception, "Could not sweep ended sessions; will retry on the next pass.");
        }
    }
}
