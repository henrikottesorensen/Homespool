using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Cameras;

/// <summary>
/// Tells the stream server which address a browser should send WebRTC media to, once at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without this, live view fails in the one way that is worst to debug.</b> go2rtc advertises the
/// addresses it can see, which inside Compose are its own container's — so a browser negotiates
/// successfully, receives an answer, and then sends media to an address that does not exist. No
/// error, no log line, a black rectangle. Naming a reachable candidate is the whole fix, and it is
/// additive: the useless ones are still advertised and simply lose.
/// </para>
/// <para>
/// <b>Why the application computes it rather than an operator writing it down.</b> The address is a
/// property of the machine and machines move — a DHCP lease changes and a value typed into
/// <c>.env</c> is silently wrong from then on. Resolving <c>PrusaConnect:PrinterHost</c> each start
/// asks the question again every time. It is also the <i>only</i> route to this machine's real
/// address from in here: every interface this process can see belongs to the container network, the
/// same limitation <see cref="PrinterCertificateNames"/> records and answers the same way.
/// </para>
/// <para>
/// <b>An <see cref="IHostedService"/> rather than a <see cref="BackgroundService"/>, and that is
/// load-bearing.</b> Writing the configuration replaces it rather than merging — see
/// <see cref="Go2RtcClient.WriteConfigAsync"/> — so every registered stream is lost, and
/// <see cref="CameraStreamReconciler"/> putting them back is what makes that acceptable. That
/// requires this to finish first, and on .NET 10 registration order does not give it: every
/// <c>BackgroundService.ExecuteAsync</c> is scheduled onto the thread pool, so two of them start
/// together on .NET 10. Hosted services started from
/// <c>StartAsync</c> are awaited in order, which does.
/// </para>
/// <para>
/// <b>Bounded, because it is on the startup path.</b> Everything here runs under a deadline of its
/// own, so a sidecar that is down or slow costs a log line rather than a server that will not come
/// up. Nothing is half-done if it expires: the write either happened or did not, and the next start
/// asks again.
/// </para>
/// </remarks>
public sealed class WebRtcConfigurer : IHostedService
{
    /// <summary>
    /// Longest this may delay startup. Generous for a container on the same network, and short
    /// enough that a sidecar which is not there is an inconvenience rather than an outage.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly WebRtcSidecarWriter _writer;
    private readonly CameraLiveAvailability _availability;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<CameraOptions> _cameras;
    private readonly IOptions<PrusaConnectOptions> _connect;
    private readonly IOptions<CertificateOptions> _certificates;
    private readonly IHostAddressResolver _resolver;
    private readonly ILogger<WebRtcConfigurer> _logger;

    public WebRtcConfigurer(WebRtcSidecarWriter writer,
                            CameraLiveAvailability availability,
                            IServiceScopeFactory scopeFactory,
                            IOptions<CameraOptions> cameras,
                            IOptions<PrusaConnectOptions> connect,
                            IOptions<CertificateOptions> certificates,
                            IHostAddressResolver resolver,
                            ILogger<WebRtcConfigurer> logger)
    {
        _writer = writer;
        _availability = availability;
        _scopeFactory = scopeFactory;
        _cameras = cameras;
        _connect = connect;
        _certificates = certificates;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        try
        {
            await ConfigureAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either shutting down, or the sidecar took longer than a startup path may wait. Live
            // view is unavailable until the next start; nothing else is affected, because stills do
            // not go through any of this.
            _logger.LogInformation(
                "The stream server was not configured for live view within {Seconds}s of startup.",
                Deadline.TotalSeconds);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns a configured override and a resolved address into the candidate to advertise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The override wins outright and is not checked against this machine</b>, deliberately: the
    /// case it exists for is a forwarded port or a tunnel, where the right answer is an address this
    /// machine does not hold and could not be persuaded to recognise. Judging it here would refuse
    /// precisely the deployments that need it.
    /// </para>
    /// <para>
    /// <b>A port is appended only when the override has none</b>, so an operator can name one and
    /// the published port is used when they do not.
    /// </para>
    /// <para>
    /// <b>An IPv6 literal needs brackets, and getting that wrong fails silently</b> — found on the
    /// board 2026-08-18, where <c>fdc2:74d8:1010::bae:8555</c> was accepted by the sidecar's
    /// configuration and then produced <i>no candidate lines at all</i> in the offer. Not an error,
    /// not a log line: an answer with nothing to connect to, which is this feature's whole failure
    /// mode arriving through the setting meant to prevent it. So "does it already carry a port" is
    /// decided by counting colons rather than looking for one, since every IPv6 address has several.
    /// </para>
    /// </remarks>
    public static string CandidateFor(string configured, int port, IReadOnlyList<IPAddress> resolved,
                                      IReadOnlyList<IPNetwork> containerNetworks)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string trimmed = configured?.Trim() ?? string.Empty;

        if (trimmed.Length > 0)
        {
            return WithPort(trimmed, port);
        }

        foreach (IPAddress address in resolved)
        {
            // Borrowed from the printer side, and the borrowing is the point rather than a shortcut:
            // "an address something outside this stack can reach" is one question, and a browser on
            // the household LAN and a printer on it are asking it identically. IPv4-only comes with
            // it, which costs an IPv6-only deployment the derivation and leaves it the override.
            if (ProvisioningBundleBuilder.CouldReachAPrinter(address, containerNetworks))
            {
                return $"{address}:{port.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// An address with a port on it, whatever shape the address arrived in.
    /// </summary>
    /// <remarks>
    /// Four cases and they are all real: a name or IPv4 with no port, the same with one, a bracketed
    /// IPv6 (with or without), and a bare IPv6 — which is the one an operator will write, because it
    /// is what every other tool prints.
    /// </remarks>
    private static string WithPort(string address, int port)
    {
        string suffix = $":{port.ToString(CultureInfo.InvariantCulture)}";

        if (address.StartsWith('['))
        {
            // Already bracketed. A port follows the closing bracket, if there is one at all.
            int close = address.IndexOf(']', StringComparison.Ordinal);

            return close >= 0 && close == address.Length - 1 ? address + suffix : address;
        }

        int colons = address.AsSpan().Count(':');

        // Two or more means an IPv6 literal that nobody has bracketed, so it cannot be carrying a
        // port: "::1:8555" is a valid address in its own right, not an address and a port, which is
        // exactly why appending one without brackets produced something the sidecar silently ignored.
        if (colons >= 2)
        {
            return $"[{address}]{suffix}";
        }

        return colons == 1 ? address : address + suffix;
    }

    private async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        CameraOptions cameras = _cameras.Value;

        IReadOnlyList<IPAddress> resolved = _connect.Value.IsPrinterAddressConfigured
            ? await _resolver.ResolveAsync(_connect.Value.PrinterHost.Trim(), cancellationToken).ConfigureAwait(false)
            : [];

        string candidate = CandidateFor(
            cameras.WebRtcCandidate, cameras.WebRtcPort, resolved, _certificates.Value.ParsedContainerNetworks);

        _availability.Candidate = candidate;

        if (candidate.Length == 0)
        {
            // Said once, at Information, because it is a real capability being absent rather than a
            // fault: everything except live view works, and the administrator's banner carries the
            // same news somewhere they will see it.
            _logger.LogInformation(
                "No WebRTC address could be worked out, so live camera view is off. Set PRINTER_HOST to a name "
                + "this machine answers to, or WEBRTC_CANDIDATE to the address and port a browser should use.");

            return;
        }

        // A scope, because the setting lives in the database and this is a singleton. Read here
        // rather than inside the writer so that the writer stays something the settings page can
        // call with a value it already has.
        bool stunEnabled;

        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            DeploymentSettingStore settings = scope.ServiceProvider.GetRequiredService<DeploymentSettingStore>();

            stunEnabled = (await settings.GetAsync(cancellationToken).ConfigureAwait(false)).WebRtcStunEnabled;
        }

        await _writer.EnsureAsync(candidate, stunEnabled, cancellationToken).ConfigureAwait(false);
    }
}
