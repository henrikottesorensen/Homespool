using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <summary>
/// Puts a changed STUN choice into the stream server while the application runs, then writes back
/// any camera stream the restart that takes may have cost.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a save needs this at all.</b> STUN is not read per use: it lives in the stream server's own
/// configuration, which <see cref="WebRtcConfigurer"/> writes once at startup. A save reaches the
/// options and nothing else, so without this the settings page would promise a change the stream
/// server never hears about - and turning STUN off, the privacy choice, would go on contacting the
/// STUN server until the next restart.
/// </para>
/// <para>
/// <b>One loop, and a burst of saves applies once</b>, with whatever the options say when it runs.
/// Applying restarts the stream server, which drops whoever is watching live - the settings page
/// warns of it - so two saves in a row must not cost two restarts.
/// </para>
/// <para>
/// <b>Asked again until it lands.</b> While the saved choice is not the one the stream server has
/// confirmed, this tries again every <see cref="RetryInterval"/>, so a save made while the sidecar
/// is down still arrives, and so does a choice the startup configurer could not deliver.
/// </para>
/// <para>
/// <b>Every camera is reconciled after a write.</b> The write is a read-modify-write of the sidecar's
/// file that does not take the lock registering a stream takes, so a camera saved in the same moment
/// can be dropped from the file and lost at the restart. The cameras' rows are the truth, and
/// <see cref="CameraStreamReconciler.ReconcileAsync"/> writes each one back from its row - the same
/// pass that runs at startup, which already waits out a restarting sidecar.
/// </para>
/// <para>
/// <b>Nothing to do without a candidate address.</b> Live view is off then, and the stream server is
/// never told anything about WebRTC.
/// </para>
/// </remarks>
public sealed class WebRtcStunApplier : BackgroundService
{
    /// <summary>
    /// How long to wait before asking an unreachable stream server again.
    /// </summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly WebRtcSidecarWriter _writer;
    private readonly CameraStreamReconciler _reconciler;
    private readonly CameraLiveAvailability _availability;
    private readonly IOptionsMonitor<CameraOptions> _cameras;
    private readonly TimeProvider _time;
    private readonly ILogger<WebRtcStunApplier> _logger;

    public WebRtcStunApplier(WebRtcSidecarWriter writer,
                             CameraStreamReconciler reconciler,
                             CameraLiveAvailability availability,
                             IOptionsMonitor<CameraOptions> cameras,
                             TimeProvider time,
                             ILogger<WebRtcStunApplier> logger)
    {
        _writer = writer;
        _reconciler = reconciler;
        _availability = availability;
        _cameras = cameras;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Brings the stream server's STUN choice to the saved one, if they differ.
    /// </summary>
    /// <returns>Whether the stream server now holds the saved choice, or has no candidate to hold it with.</returns>
    internal async Task<bool> ApplyAsync(CancellationToken cancellationToken)
    {
        string candidate = _availability.Candidate;
        WebRtcStunChoice wanted = WebRtcStunChoice.From(_cameras.CurrentValue);

        if (candidate.Length == 0 || wanted == _availability.StunInForce)
        {
            return true;
        }

        if (!await _writer.EnsureAsync(candidate, wanted, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "The stream server could not be given the new STUN choice; asking again in {Seconds}s.",
                RetryInterval.TotalSeconds);

            return false;
        }

        await _reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using IDisposable? listening = _cameras.OnChange(_ => _changed.Writer.TryWrite(true));

        try
        {
            bool settled = await ApplyAsync(stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitAsync(settled, stoppingToken).ConfigureAwait(false);

                settled = await ApplyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. A choice not yet delivered is delivered by the next start's configurer.
        }
    }

    /// <summary>
    /// Until the options change - or, while a choice is still owed, until the retry interval passes.
    /// </summary>
    private async Task WaitAsync(bool settled, CancellationToken cancellationToken)
    {
        if (settled)
        {
            await _changed.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            return;
        }

        using CancellationTokenSource retry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task changed = _changed.Reader.WaitToReadAsync(retry.Token).AsTask();
        Task due = Task.Delay(RetryInterval, _time, retry.Token);

        await Task.WhenAny(changed, due).ConfigureAwait(false);
        await retry.CancelAsync().ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        _changed.Reader.TryRead(out _);
    }
}
