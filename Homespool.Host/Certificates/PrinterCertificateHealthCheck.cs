using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Certificates;

/// <summary>
/// Reports when the printer certificate has stopped matching the machine it belongs to, so that
/// printers refusing to connect has an explanation before anyone goes looking for one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The leaf is issued once and then frozen</b>, deliberately: reissuing on every change would drop
/// live connections whenever an interface appeared, make the certificate a function of what the
/// machine happened to look like at boot, and silently expand what this server claims to be. The cost
/// of that choice is exactly this — a moved DHCP lease leaves a certificate nobody can verify — and
/// this is what makes the cost payable. Detect and explain; the operator decides.
/// </para>
/// <para>
/// <b>A health check rather than a new notification path</b>, because <c>HealthBanner</c> derives its
/// items from the health report: registering here puts drift in <c>/health</c> for whoever has
/// monitoring and on an administrator's screen for whoever does not. It is the channel already
/// reserved for "the service looks fine and is quietly failing", which is what this is — a printer
/// that cannot complete a handshake reports nothing to anyone, and the server sees no connection to
/// be concerned about.
/// </para>
/// <para>
/// <b>Never tagged for liveness.</b> A restart re-reads the same file, so nothing here is a fault a
/// restart would fix, and <c>/health/live</c> exists to keep such things out of a restart loop.
/// </para>
/// <para>
/// This class gathers; <see cref="PrinterCertificateDrift"/> judges. That split is what lets the rule
/// "every address in the certificate has gone" be tested at all, since producing it for real means
/// moving a DHCP lease.
/// </para>
/// </remarks>
public sealed class PrinterCertificateHealthCheck : IHealthCheck
{
    private readonly PrinterCertificateAuthority _authority;
    private readonly PrusaConnectOptions _connect;
    private readonly CertificateOptions _certificates;
    private readonly IHostAddressResolver _resolver;
    private readonly TimeProvider _time;

    public PrinterCertificateHealthCheck(PrinterCertificateAuthority authority,
                                         IOptions<PrusaConnectOptions> connect,
                                         IOptions<CertificateOptions> certificates,
                                         IHostAddressResolver resolver,
                                         TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(certificates);

        _authority = authority;
        _connect = connect.Value;
        _certificates = certificates.Value;
        _resolver = resolver;
        _time = time;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_connect.PrinterTls)
        {
            return Result(PrinterCertificateDrift.Evaluate(
                              tlsEnabled: false, null, [], [], null, null, _time.GetUtcNow()));
        }

        using X509Certificate2? leaf = _authority.LoadLeafIfIssued();
        using X509Certificate2? authority = leaf is null ? null : _authority.EnsureAuthority();

        IReadOnlyList<string> covered = leaf is null ? [] : PrinterCertificateAuthority.NamesOf(leaf);
        IReadOnlyList<string> current = await PrinterCertificateNames.ForThisMachineAsync(
            _connect, _certificates.ParsedContainerNetworks, _resolver, cancellationToken);

        PrinterCertificateVerdict verdict = PrinterCertificateDrift.Evaluate(
            tlsEnabled: true,
            _connect.IsPrinterAddressConfigured ? _connect.PrinterHost : null,
            covered,
            current,
            leaf?.NotAfter.ToUniversalTime(),
            authority?.NotAfter.ToUniversalTime(),
            _time.GetUtcNow());

        return Result(verdict, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["covered"] = string.Join(", ", covered),
            ["current"] = string.Join(", ", current),
            ["state"] = verdict.State.ToString(),
        });
    }

    private static HealthCheckResult Result(PrinterCertificateVerdict verdict,
                                            IReadOnlyDictionary<string, object>? data = null)
    {
        return verdict.IsProblem ?
            HealthCheckResult.Degraded(verdict.Description, data: data) :
            HealthCheckResult.Healthy(verdict.Description, data);
    }
}
