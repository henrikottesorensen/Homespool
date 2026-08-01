using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.Certificates;
using Homespool.Host.PrusaConnect;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Services;

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

    public DeploymentExposureHealthCheck(IOptions<PrusaConnectOptions> connect,
                                         IOptions<CertificateOptions> certificates,
                                         IHostAddressResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(certificates);

        _connect = connect.Value;
        _certificates = certificates.Value;
        _resolver = resolver;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                                                          CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IPAddress> resolved = _connect.IsPrinterAddressConfigured
            ? await _resolver.ResolveAsync(_connect.PrinterHost.Trim(), cancellationToken)
            : [];

        ExposureVerdict verdict = DeploymentExposure.EvaluatePrinterTransport(
            _connect.PrinterTls,
            _connect.IsPrinterAddressConfigured ? _connect.PrinterHost : null,
            resolved,
            _certificates.ParsedContainerNetworks);

        return verdict.IsProblem
            ? HealthCheckResult.Degraded(verdict.Description)
            : HealthCheckResult.Healthy(verdict.Description);
    }
}
