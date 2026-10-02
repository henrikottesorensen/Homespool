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

namespace Homespool.Host.Cameras;

/// <summary>
/// Makes the stream server's streams match the cameras Homespool knows about, once at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every camera, every start, and usually nothing to do.</b> go2rtc writes what it is told into
/// its own config and reloads it - measured 2026-08-08 - so streams survive an ordinary restart and
/// each one is left alone. What this catches is a sidecar that does not match the cameras: its file
/// replaced or emptied, a camera saved while the sidecar was down, a stack brought up from a
/// database restored onto a fresh sidecar, or a stream holding a source that a rule made since
/// refuses - which the file would otherwise keep for good.
/// </para>
/// <para>
/// <b>It writes nothing itself.</b> Each camera goes through <see cref="CameraStreamSync"/>, which
/// reads the camera's row at the moment its stream is written. This waits for the sidecar for up to
/// <see cref="ListingPatience"/>, and a camera edited or removed in that wait reaches the sidecar as
/// it is after the edit, not as it was when this started.
/// </para>
/// <para>
/// <b>It removes only what Homespool made.</b> A stream named after a camera uuid that no camera has
/// is one this application lost track of - deleted while the sidecar could not be told - and
/// <see cref="CameraStreamSweeper"/> removes those before anything is added. Any other stream the
/// sidecar holds might be somebody's hand-added experiment, and is left alone.
/// </para>
/// <para>
/// <b>It also warms the codec memo</b>, because that memo is empty on every start and the first
/// page would otherwise pay for the probe - and lose, on the case that matters: measured
/// 2026-08-20, the first DESCRIBE against a cold USB camera during stack start ran past its
/// deadline, and the live button was missing until somebody reloaded. Probed here instead, where
/// nobody is waiting.
/// </para>
/// <para>
/// Follows <c>PrintFileReconciler</c>: a one-shot at startup rather than a loop, because the thing
/// it heals only changes when something outside the application does.
/// </para>
/// <para>
/// <b>It can start while the sidecar is restarting</b>, because <see cref="WebRtcConfigurer"/> runs
/// just before it and restarts the sidecar whenever the WebRTC address changed. go2rtc answers the
/// restart and then re-executes itself, so for a while nothing is listening at all, and for a while
/// after it is listening it refuses every source with <c>streams: source not supported</c> - its API
/// and its stream list come up before the modules that register the source schemes. Both are
/// milliseconds on a fast machine, and took up to half a second with the sidecar held to a tenth of a
/// core. So the listing is asked for again until it answers, and a camera refused or unanswered is
/// tried once more after <see cref="ProbeRetryDelay"/>.
/// </para>
/// </remarks>
public sealed class CameraStreamReconciler : BackgroundService
{
    /// <summary>
    /// How long to wait before giving a failed startup probe or registration its one retry. The
    /// failure it answers is racing the rest of the stack's start, and a few seconds is what that
    /// race needs.
    /// </summary>
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to ask again for a listing the sidecar did not give.
    /// </summary>
    private static readonly TimeSpan ListingRetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to go on asking for the listing before leaving the cameras to their next save. Far
    /// longer than a restart takes, and short enough that a sidecar which is really not there is
    /// given up on while the log still reads as startup.
    /// </summary>
    private static readonly TimeSpan ListingPatience = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Go2RtcClient _streamServer;
    private readonly CameraStreamSync _sync;
    private readonly CameraLiveAvailability _liveView;
    private readonly TimeProvider _time;
    private readonly ILogger<CameraStreamReconciler> _logger;

    public CameraStreamReconciler(IServiceScopeFactory scopeFactory,
                                  Go2RtcClient streamServer,
                                  CameraStreamSync sync,
                                  CameraLiveAvailability liveView,
                                  TimeProvider time,
                                  ILogger<CameraStreamReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _streamServer = streamServer;
        _sync = sync;
        _liveView = liveView;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Asked for even with no cameras, because the sweep below needs it: the removal of the
            // last camera is the one it would otherwise never see.
            IReadOnlySet<string>? known = await ListStreamNamesAsync(stoppingToken).ConfigureAwait(false);

            using IServiceScope scope = _scopeFactory.CreateScope();
            HomespoolDbContext database = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

            // After the wait, and only which cameras there are: each one's row is read again by the
            // sync, at the moment its stream is written, so what a camera is comes from then.
            List<Guid> cameras = await database.Cameras
                                               .Select(camera => camera.Uuid)
                                               .ToListAsync(stoppingToken)
                                               .ConfigureAwait(false);

            // Null means the sidecar could not be asked, which is not the same as it knowing
            // nothing. Re-registering every camera against a server that is merely still starting
            // would be work at best and a thundering herd at worst.
            if (known is null)
            {
                if (cameras.Count > 0)
                {
                    _logger.LogInformation(
                        "The stream server could not be reached within {Seconds}s of startup; {Count} cameras will be " +
                        "registered when one is next saved.",
                        ListingPatience.TotalSeconds,
                        cameras.Count);
                }

                return;
            }

            // After the wait for a listing, so a sidecar restarting under the configurer is swept
            // once it is back rather than found empty-handed; before anything is registered, so a
            // device an orphan still holds is free when its camera's own stream arrives. The rows
            // the sweep reads are its own, taken after this listing.
            int swept = await scope.ServiceProvider.GetRequiredService<CameraStreamSweeper>()
                                   .SweepAsync(known, stoppingToken)
                                   .ConfigureAwait(false);

            if (swept > 0)
            {
                _logger.LogInformation("Removed {Count} streams no camera owns from the stream server.", swept);
            }

            if (cameras.Count == 0)
            {
                return;
            }

            // Every camera, not only the ones the sidecar is missing: one it holds may hold a source
            // the camera no longer has, or one a rule made since then refuses, and the sidecar's file
            // keeps either across every restart until something writes over it.
            int restored = 0;
            List<Guid> retry = [];

            foreach (Guid camera in cameras)
            {
                switch ((await _sync.SyncAsync(camera, stoppingToken).ConfigureAwait(false)).Outcome)
                {
                    case StreamSyncOutcome.Registered:
                        restored++;
                        break;

                    // A sidecar that has only just come back refuses every source for a moment, in
                    // the same words it refuses a bad one, so a refusal here earns the retry too. A
                    // source that really is refused costs one more request and one more log line.
                    case StreamSyncOutcome.Unavailable:
                    case StreamSyncOutcome.SourceRefused:
                        retry.Add(camera);
                        break;
                }
            }

            if (retry.Count > 0)
            {
                await Task.Delay(ProbeRetryDelay, _time, stoppingToken).ConfigureAwait(false);

                foreach (Guid camera in retry)
                {
                    if ((await _sync.SyncAsync(camera, stoppingToken).ConfigureAwait(false)).Outcome ==
                        StreamSyncOutcome.Registered)
                    {
                        restored++;
                    }
                }
            }

            if (restored > 0)
            {
                _logger.LogInformation(
                    "Registered {Count} cameras the stream server was missing or held an older source for.",
                    restored);
            }

            // Sequentially, deliberately: two probes against one USB device would contend for it,
            // and nobody is waiting on this path.
            foreach (Guid camera in cameras)
            {
                if (await _liveView.HowToWatchAsync(camera, stoppingToken).ConfigureAwait(false) !=
                    LiveTransport.None)
                {
                    continue;
                }

                // One retry. A None here is either a definite "no transport" - in which case the
                // memo answers and the second ask costs nothing - or a camera that did not answer,
                // most likely because it is still waking up alongside everything else.
                await Task.Delay(ProbeRetryDelay, _time, stoppingToken).ConfigureAwait(false);
                _ = await _liveView.HowToWatchAsync(camera, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the sweep finished. Nothing is half-done: each registration is
            // its own request, and the next start does this again.
        }
    }

    /// <summary>
    /// The sidecar's stream names, asked for until it answers or <see cref="ListingPatience"/> runs
    /// out - or <see langword="null"/> if it never did.
    /// </summary>
    private async Task<IReadOnlySet<string>?> ListStreamNamesAsync(CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();

        while (true)
        {
            IReadOnlySet<string>? known = await _streamServer.ListStreamNamesAsync(cancellationToken)
                                                             .ConfigureAwait(false);

            if (known is not null || _time.GetElapsedTime(started) >= ListingPatience)
            {
                return known;
            }

            await Task.Delay(ListingRetryInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
