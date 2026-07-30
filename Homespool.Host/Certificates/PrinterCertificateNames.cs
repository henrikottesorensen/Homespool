using System.Collections.Generic;
using System.Linq;
using System.Net;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Certificates;

/// <summary>
/// Every name a printer might be told to dial this server by — what a certificate issued right now
/// would cover.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything plausible, rather than one address chosen correctly.</b> The leaf covers what
/// <c>PrusaConnect:PrinterHost</c> says <i>and</i> every address this machine can see, so an operator
/// picking the wrong one costs a re-downloaded bundle instead of a re-provisioned printer. That is the
/// multi-name hedge that makes a moved DHCP lease survivable, doing a second job — and it is why
/// nothing asks the operator to name this machine at first run (<c>notes/tls-by-default.md</c>,
/// "nobody stores the answer").
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
    /// <param name="connect">Supplies the configured printer address, which leads the list.</param>
    /// <param name="containerNetworks">
    /// The deployment's own internal ranges, so an address only the container can reach is described
    /// as such rather than offered as an equal.
    /// </param>
    public static IReadOnlyList<string> ForThisMachine(PrusaConnectOptions connect,
                                                       IReadOnlyList<IPNetwork> containerNetworks)
    {
        List<string> names = [];

        if (connect?.IsPrinterAddressConfigured == true)
        {
            names.Add(connect.PrinterHost.Trim());
        }

        names.AddRange(PrinterAddressSuggestion.Gather(containerNetworks).Select(suggestion => suggestion.Value));

        return [.. names.Distinct(System.StringComparer.OrdinalIgnoreCase)];
    }
}
