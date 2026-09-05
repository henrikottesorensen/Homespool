using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Certificates;

/// <summary>
/// Every name a printer might be told to reach this server by — what a certificate issued right now
/// would cover.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything plausible, rather than one address chosen correctly.</b> The leaf covers what
/// <c>PrusaConnect:PrinterHost</c> says <i>and</i> every address this machine can see, so an operator
/// picking the wrong one costs a re-downloaded bundle instead of a re-provisioned printer. That is the
/// multi-name hedge that makes a moved DHCP lease survivable, doing a second job — and it is why
/// nothing asks the operator to name this machine at first run - nobody stores the answer.
/// </para>
/// <para>
/// <b>One definition, two callers, deliberately.</b> Startup issues the first certificate from this,
/// and drift detection asks whether the certificate still matches it. Two copies of "the names we
/// would use" would eventually disagree, and the symptom would be a deployment told it has drifted by
/// a rule that differs from the one that issued its certificate.
/// </para>
/// </remarks>
public static class PrinterCertificateNames
{
    /// <summary>
    /// The names, best first — empty if this machine has no usable address and none is configured.
    /// </summary>
    /// <remarks>
    /// The configured host leads because <see cref="PrinterCertificateAuthority"/> takes the first
    /// name as the subject: the one an operator deliberately set is the one worth seeing when a human
    /// inspects the certificate. Callers decide what an empty list means — at startup it is a warning
    /// and a fallback, on the reissue page it is a refusal.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <b>A detected name that no printer could use does not go in.</b> Inside a container, detection
    /// sees the container: its bridge address and its own hostname. Neither is a name a printer can
    /// route to, so covering them hedges nothing - the hedge is for an operator who picked the wrong
    /// <i>reachable</i> name, not for names that are wrong by construction. Leaving them out keeps the
    /// certificate honest about what it vouches for, and stops the leaf presented on the printer port
    /// telling every unauthenticated connection what this deployment's internal network looks like.
    /// </para>
    /// <para>
    /// <b>The configured host is never filtered</b>, resolvable or not. It is the operator's declared
    /// answer, and a name that this machine cannot resolve from where it stands is routinely the right
    /// one from the printer's side of the network - which is exactly the situation inside a container.
    /// </para>
    /// <para>
    /// <b>It is also expanded: what it resolves to goes in beside it.</b> Detection alone cannot supply
    /// that in the deployment this project treats as primary - inside a container the only address on
    /// any interface is the container's own, so the multi-name hedge this class describes covers exactly
    /// one name and hedges nothing. Resolving the configured host is the one route to the machine's real
    /// address from in there, and it costs the lookup that was already being made for its neighbours.
    /// </para>
    /// <para>
    /// <b>Additive when it answers, and never a precondition.</b> A name that resolves to nothing keeps
    /// its place by the paragraph above; expansion only ever adds. Inverting that - covering the name
    /// only once it resolves - would drop precisely the LAN names a container cannot see, which is the
    /// bug the filtering rule below was written to avoid.
    /// </para>
    /// </remarks>
    /// <param name="connect">Supplies the configured printer address, which leads the list.</param>
    /// <param name="containerNetworks">The deployment's own internal ranges.</param>
    /// <param name="resolver">
    /// Answers what a hostname points at - which decides whether a detected name is dropped, and what
    /// the configured one is covered alongside. An address answers for itself.
    /// </param>
    /// <param name="cancellationToken">The usual.</param>
    public static async Task<IReadOnlyList<string>> ForThisMachineAsync(PrusaConnectOptions connect,
                                                                        IReadOnlyList<IPNetwork> containerNetworks,
                                                                        IHostAddressResolver resolver,
                                                                        CancellationToken cancellationToken)
    {
        List<string> names = [];

        if (connect?.IsPrinterAddressConfigured == true)
        {
            string configured = connect.PrinterHost.Trim();

            names.Add(configured);

            foreach (IPAddress address in await resolver.ResolveAsync(configured, cancellationToken))
            {
                if (ProvisioningBundleBuilder.CouldReachAPrinter(address, containerNetworks))
                {
                    names.Add(address.ToString());
                }
            }
        }

        foreach (PrinterAddressSuggestion suggestion in PrinterAddressSuggestion.Gather(containerNetworks))
        {
            IReadOnlyList<IPAddress> resolved = await resolver.ResolveAsync(suggestion.Value, cancellationToken);

            if (!ProvisioningBundleBuilder.IsUnreachableByPrinters(resolved, containerNetworks))
            {
                names.Add(suggestion.Value);
            }
        }

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Whether a name resolved from inside this process points at nothing but this process's own
    /// machine — an answer, but one nothing outside can use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case, precisely:</b> Debian and Raspberry Pi OS write <c>127.0.1.1 &lt;hostname&gt;</c>
    /// into <c>/etc/hosts</c>. When the hostname is the same name the operator configured as the
    /// printer host, Docker's embedded DNS forwards the container's lookup to the host's resolver,
    /// which answers from that file — so from in here the configured name is loopback, while the
    /// router answers the LAN address to every printer and browser. Every filter here correctly drops
    /// loopback, and the detected set collapses to the configured name alone: the reissue page then
    /// calls every other name on the certificate one this machine "no longer answers on", and live
    /// view has no address. Both are false, and neither says why.
    /// </para>
    /// <para>
    /// <b>Only loopback, not "includes loopback".</b> A name that resolves to loopback and a LAN
    /// address is unusual but fine — the LAN address is used. The problem is the answer being
    /// loopback and nothing else, which is what the hosts-file line produces.
    /// </para>
    /// </remarks>
    /// <param name="resolved">What the resolver answered; empty means it answered nothing.</param>
    public static bool ResolvesOnlyToLoopback(IReadOnlyList<IPAddress> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return resolved.Count > 0 && resolved.All(IPAddress.IsLoopback);
    }

    /// <summary>
    /// <see cref="ResolvesOnlyToLoopback"/> for the configured printer host; false when none is set.
    /// </summary>
    public static async Task<bool> ConfiguredHostResolvesOnlyToLoopbackAsync(PrusaConnectOptions connect,
                                                                             IHostAddressResolver resolver,
                                                                             CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        if (connect?.IsPrinterAddressConfigured != true)
        {
            return false;
        }

        return ResolvesOnlyToLoopback(await resolver.ResolveAsync(connect.PrinterHost.Trim(), cancellationToken));
    }
}
