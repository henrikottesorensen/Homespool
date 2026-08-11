using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Cameras;

/// <summary>
/// Makes the stream server's streams match the cameras Homespool knows about, once at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not needed for an ordinary restart.</b> go2rtc writes what it is told into its own config and
/// reloads it - measured 2026-08-08 - so streams survive on their own. This exists for the cases
/// where they do not: its volume replaced, a camera added while the sidecar was down, or a stack
/// brought up from a database restored onto a fresh sidecar.
/// </para>
/// <para>
/// <b>It adds and never removes.</b> A stream present in the sidecar and absent here might be
/// somebody's hand-added experiment, and deleting other people's configuration to enforce a
/// symmetry nobody asked for is the kind of tidiness that loses work. Cameras deleted through
/// Homespool are removed at the point of deletion, where the intent is unambiguous.
/// </para>
/// <para>
/// Follows <c>PrintFileReconciler</c>: a one-shot at startup rather than a loop, because the thing
/// it heals only changes when something outside the application does.
/// </para>
/// </remarks>
public sealed class CameraStreamReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Go2RtcClient _streamServer;
    private readonly ILogger<CameraStreamReconciler> _logger;

    public CameraStreamReconciler(IServiceScopeFactory scopeFactory,
                                  Go2RtcClient streamServer,
                                  ILogger<CameraStreamReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _streamServer = streamServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            List<Camera> cameras = await database.Cameras
                                                 .AsNoTracking()
                                                 .ToListAsync(stoppingToken)
                                                 .ConfigureAwait(false);

            if (cameras.Count == 0)
            {
                return;
            }

            IReadOnlySet<string>? known = await _streamServer.ListStreamNamesAsync(stoppingToken)
                                                             .ConfigureAwait(false);

            // Null means the sidecar could not be asked, which is not the same as it knowing
            // nothing. Re-registering every camera against a server that is merely still starting
            // would be work at best and a thundering herd at worst.
            if (known is null)
            {
                _logger.LogInformation(
                    "The stream server could not be reached at startup; {Count} cameras will be "
                    + "registered when one is next saved.",
                    cameras.Count);
                return;
            }

            IEnumerable<Camera> missing =
                cameras.Where(camera => !known.Contains(camera.Uuid.ToString("D", CultureInfo.InvariantCulture)));

            int restored = 0;

            foreach (Camera camera in missing)
            {
                if (await _streamServer.PutStreamAsync(camera.Uuid, camera.Source, stoppingToken)
                                       .ConfigureAwait(false))
                {
                    restored++;
                }
            }

            if (restored > 0)
            {
                _logger.LogInformation("Registered {Count} cameras the stream server did not have.", restored);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the sweep finished. Nothing is half-done: each registration is
            // its own request, and the next start does this again.
        }
    }
}
