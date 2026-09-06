using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Health;

/// <summary>
/// Reports a deployment that is handing printer tokens to the internet in clear text.
/// </summary>
/// <remarks>
/// <para>
/// The half of exposure that configuration can answer, so it belongs with the other checks rather
/// than in a page: <see cref="HealthBanner"/> shows it to an administrator, and <c>/health</c> carries
/// it for anything watching. The other half — an administrator reading the site over plain HTTP — can
/// only be seen from a request, and lives in <c>HealthBannerViewComponent</c>.
/// </para>
/// <para>
/// <b>Degraded, never Unhealthy.</b> The service is doing exactly what it was told to do, and
/// Unhealthy turns <c>/health</c> into a 503 that reads as "down" to whatever is watching. This is a
/// judgement about a configuration, not a fault.
/// </para>
/// </remarks>
public sealed class DeploymentExposureHealthCheck : IHealthCheck
{
    private readonly PrusaConnectOptions _connect;
    private readonly CertificateOptions _certificates;
    private readonly IHostAddressResolver _resolver;
    private readonly Printing.PrinterConnectionRegistry _connections;
    private readonly Data.HomespoolDbContext _context;

    public DeploymentExposureHealthCheck(IOptionsMonitor<PrusaConnectOptions> connect,
                                         IOptions<CertificateOptions> certificates,
                                         IHostAddressResolver resolver,
                                         Printing.PrinterConnectionRegistry connections,
                                         Data.HomespoolDbContext context)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(certificates);

        _connect = connect.CurrentValue;
        _certificates = certificates.Value;
        _resolver = resolver;
        _connections = connections;
        _context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                                                          CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IPAddress> resolved = _connect.IsPrinterAddressConfigured ?
            await _resolver.ResolveAsync(_connect.PrinterHost.Trim(), cancellationToken) :
            [];

        ExposureVerdict verdict = DeploymentExposure.EvaluatePrinterTransport(
            _connect.PrinterTls,
            await NeedlesslyOnPlaintextAsync(cancellationToken),
            _connect.IsPrinterAddressConfigured ? _connect.PrinterHost : null,
            resolved,
            _certificates.ParsedContainerNetworks);

        return verdict.IsProblem ? HealthCheckResult.Degraded(verdict.Description) : HealthCheckResult.Healthy(verdict.Description);
    }

    /// <summary>
    /// How many printers are on the plaintext listener while reporting firmware that need not be.
    /// </summary>
    /// <remarks>
    /// <b>The connection answers which listener, the row answers which firmware</b>, and neither can
    /// answer the other. The listener is a property of a live socket and is never stored; the version
    /// is refreshed on every <c>INFO</c> and outlives the connection. The database is only asked about
    /// printers already known to be on the plaintext listener, so a deployment that has never opened
    /// one does no query at all.
    /// </remarks>
    private async Task<int> NeedlesslyOnPlaintextAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<int> onPlaintext = _connections.PrintersOnPlaintextListener();

        if (onPlaintext.Count == 0)
        {
            return 0;
        }

        List<string?> firmwares = await _context.Printers
                                                .Where(printer => onPlaintext.Contains(printer.Id))
                                                .Select(printer => printer.Firmware)
                                                .ToListAsync(cancellationToken);

        return firmwares.Count(PrusaConnect.PrinterFirmwareVersion.CanLoadCustomCertificate);
    }
}
