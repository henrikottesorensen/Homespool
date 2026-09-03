using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Certificates;

/// <summary>
/// The certificate work that runs on the startup path, so <c>Program.cs</c> says when it happens
/// rather than how.
/// </summary>
/// <remarks>
/// <c>Registration.cs</c> beside this is the same idea for Data Protection: the configuration for a
/// thing lives beside the thing.
/// </remarks>
public static class PrinterCertificateStartup
{
    /// <summary>
    /// Mints the authority and the leaf nginx presents to printers, unless this deployment has turned
    /// printer TLS off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Before the first request rather than on demand</b>, and now for nginx's sake rather than
    /// Kestrel's: the proxy reads <c>printer-leaf.pem</c> off the shared volume when it starts, and
    /// <c>compose.yaml</c> holds it back until this container reports healthy, which is after this
    /// runs. A leaf minted lazily would be minted after the thing that needs it had already given up.
    /// </para>
    /// <para>
    /// It writes PEM as well as PKCS#12 (<see cref="PrinterCertificateAuthority"/>), with
    /// the leaf <i>alone</i> in the certificate file — firmware's
    /// <c>x509_crt_check_ee_locally_trusted</c> requires exactly one certificate presented, and a
    /// terminator that appends the authority fails in a way that reads as a protocol bug.
    /// </para>
    /// </remarks>
    /// <param name="app">The built application, for its service provider.</param>
    public static void EnsurePrinterCertificate(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!PrinterTransportIsSecure(app.Services))
        {
            // Plaintext, so the wire can be read. Nothing else in this project can produce a legible
            // capture of the printer protocol: TLS is the point of the path, and a capture of it is
            // ciphertext.
            //
            // The application's own logger, not Serilog's static: a host no longer replaces that
            // static with its own logger, so what sits behind it here is the bootstrap logger, carrying
            // none of the sinks or levels the deployment configured. This is a line an operator has to
            // be able to route.
            app.Logger
               .LogWarning("Printers reach this deployment in PLAINTEXT because PrusaConnect:PrinterTls is false, and no "
                           + "certificate is issued while it is off. Every printer token crosses the network in clear, in "
                           + "both directions - the one on the USB stick and the one issued at claim. This is for a capture "
                           + "or a rig on a network you control; it is not a deployment setting. The proxy has no printer "
                           + "certificate to serve either, so publish Listeners:PrinterPort directly.");

            return;
        }

        PrinterCertificateAuthority authority = app.Services.GetRequiredService<PrinterCertificateAuthority>();

        // The authority explicitly, not only via the leaf: with an existing leaf nothing below opens
        // the authority's key, so a wrong or missing passphrase would otherwise surface days later,
        // when somebody builds a provisioning bundle - instead of here, at the boot right after the
        // configuration changed.
        authority.EnsureAuthority().Dispose();
        authority.EnsureLeaf(LeafNames(app.Services)).Dispose();
    }

    /// <summary>
    /// Whether printers reach this deployment over TLS — which is one question, not two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>PrusaConnect:PrinterTls</c> decides whether a leaf is minted as well as what the ini
    /// says</b>, and that is deliberate rather than convenient. It no longer decides the listener,
    /// because the listener is plain HTTP either way — what changes is whether nginx stands in front
    /// of it holding the leaf. The two ends still cannot disagree: with it off nothing is issued, so
    /// the proxy has nothing to present even if an operator wired it up anyway.
    /// </para>
    /// <para>
    /// Two settings for one fact is the failure this project keeps finding: they disagree, every
    /// printer fails to connect, and neither value is wrong on its own so nothing can report it. One
    /// setting cannot disagree with itself.
    /// </para>
    /// <para>
    /// It also decides whether forwarded headers are honoured on the printer listener — see
    /// <c>Middleware/Registration.cs</c>. With nginx in front, <c>X-Real-IP</c> on that listener comes
    /// from the proxy and nothing else can reach the port; without it, a printer connects directly and
    /// the same header is written by whoever connected.
    /// </para>
    /// </remarks>
    /// <param name="services">The built container, which is where the bound options live.</param>
    /// <returns>True when nginx terminates printer TLS in front of this process.</returns>
    public static bool PrinterTransportIsSecure(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetRequiredService<IOptionsMonitor<PrusaConnect.PrusaConnectOptions>>().CurrentValue.PrinterTls;
    }

    /// <summary>
    /// Every name a printer might be told to reach this server by, for the first run's leaf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything plausible, rather than one address chosen correctly.</b> The leaf covers what
    /// <c>PrusaConnect:PrinterHost</c> says <i>and</i> every address this machine can see, so the
    /// operator picking the wrong one costs a re-downloaded provisioning bundle instead of a
    /// re-provisioned printer. That is the same multi-name hedge that makes a moved DHCP lease
    /// survivable, doing a second job - and it is why nothing asks the operator to name this machine
    /// at first run - nobody stores the answer.
    /// </para>
    /// <para>
    /// The configured host goes first because <see cref="PrinterCertificateAuthority"/>
    /// takes the first name as the subject: the one an operator deliberately set is the one worth
    /// seeing when a human inspects the certificate.
    /// </para>
    /// </remarks>
    [SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits",
                     Justification =
                         "Runs during startup, before the server accepts connections, so there is nothing to deadlock against and no asynchronous caller to yield to.")]
    private static IReadOnlyList<string> LeafNames(IServiceProvider services)
    {
        PrusaConnect.PrusaConnectOptions connect =
            services.GetRequiredService<IOptions<PrusaConnect.PrusaConnectOptions>>().Value;

        CertificateOptions certificates = services.GetRequiredService<IOptions<CertificateOptions>>().Value;

        // Blocking here is deliberate and bounded: this runs on the startup path, before the first
        // request and before the proxy is let through to read the leaf, so there is nothing to be
        // asynchronous for yet. The resolver caps each lookup, and only detected names are asked -
        // the configured host is taken as given.
        List<string> names =
        [
            .. PrinterCertificateNames.ForThisMachineAsync(
                connect,
                certificates.ParsedContainerNetworks,
                services.GetRequiredService<IHostAddressResolver>(),
                CancellationToken.None).GetAwaiter().GetResult()
        ];

        if (names.Count == 0)
        {
            // A machine with no usable address and no configured host: the proxy still has to have
            // something to present, but nothing will be able to verify it, so say why now rather than
            // leaving an unexplained TLS failure at the printer.
            // Categorised by the class this is about rather than the options bag it reads: only an
            // IServiceProvider is in scope here, so the WebApplication's own logger - used for the
            // same job above - is not available.
            services.GetRequiredService<ILogger<PrinterCertificateAuthority>>()
                    .LogWarning("No printer-facing address could be detected and PrusaConnect:PrinterHost is not set, so "
                                + "the printer certificate covers only localhost. Set PrusaConnect:PrinterHost and delete "
                                + "the generated printer-leaf.pem and printer-leaf.key.pem to have one issued that "
                                + "printers can actually verify.");

            names.Add("localhost");
        }

        return names;
    }
}
