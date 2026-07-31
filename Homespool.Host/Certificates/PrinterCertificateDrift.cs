using System;
using System.Collections.Generic;
using System.Linq;

namespace Homespool.Host.Certificates;

/// <summary>
/// Decides whether a printer certificate has stopped matching its machine — the judgement, with no
/// filesystem, no network and no clock of its own.
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="PrinterCertificateHealthCheck"/> for the reason
/// <see cref="PrinterAddressSuggestion.Classify"/> is split from <see cref="PrinterAddressSuggestion.Gather"/>:
/// the part with judgement in it can then be tested exhaustively, including the cases a machine
/// cannot be asked to produce on demand. "Every address in the certificate has gone" is precisely
/// such a case — reproducing it for real means moving a DHCP lease.
/// </para>
/// <para>
/// The order of the checks is the order the problems bite, not their severity: an address printers
/// cannot verify stops provisioning today, while an expiry is a date in the diary.
/// </para>
/// </remarks>
public static class PrinterCertificateDrift
{
    /// <summary>How long before the leaf expires to start saying so.</summary>
    /// <remarks>
    /// Replacing it costs a button and a proxy reload and nothing at any printer, so the notice only
    /// has to beat the gap between an administrator's visits.
    /// </remarks>
    public static readonly TimeSpan LeafExpiryWarning = TimeSpan.FromDays(30);

    /// <summary>How long before the authority expires to start saying so — a year, and not generously.</summary>
    /// <remarks>
    /// The authority is each printer's entire trust store and cannot be delivered over the network, so
    /// replacing it means a USB visit to every printer and none of them can connect until visited. The
    /// default authority lasts fifteen years, so this warning is the first time anyone will have
    /// thought about it since the deployment was built.
    /// </remarks>
    public static readonly TimeSpan AuthorityExpiryWarning = TimeSpan.FromDays(365);

    /// <summary>
    /// The verdict on a certificate described by these facts.
    /// </summary>
    /// <param name="tlsEnabled">Whether printers use TLS at all.</param>
    /// <param name="configuredHost">The address printers are told to dial, or null if none is set.</param>
    /// <param name="covered">What the certificate vouches for.</param>
    /// <param name="current">What a certificate issued now would cover.</param>
    /// <param name="leafExpires">When the leaf expires, or null if there is none.</param>
    /// <param name="authorityExpires">When the authority expires, or null if there is none.</param>
    /// <param name="now">The clock.</param>
    public static PrinterCertificateVerdict Evaluate(bool tlsEnabled,
                                                     string? configuredHost,
                                                     IReadOnlyList<string> covered,
                                                     IReadOnlyList<string> current,
                                                     DateTimeOffset? leafExpires,
                                                     DateTimeOffset? authorityExpires,
                                                     DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(covered);
        ArgumentNullException.ThrowIfNull(current);

        if (!tlsEnabled)
        {
            // Startup already warns about this, every time, and loudly. A permanent banner repeating
            // it is how a warning becomes wallpaper.
            return new(PrinterCertificateState.NotInUse, "Printers connect over plain HTTP, so no certificate is in use.");
        }

        if (covered.Count == 0 || leafExpires is null)
        {
            return new(PrinterCertificateState.Missing,
                "No printer certificate has been issued, so no printer can verify this server. It is normally created "
                + "at startup - check the log for why it was not.");
        }

        if (!string.IsNullOrWhiteSpace(configuredHost)
            && !covered.Contains(configuredHost.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return new(PrinterCertificateState.ConfiguredAddressUncovered,
                $"The printer certificate does not cover {configuredHost.Trim()}, which is the address printers are "
                + $"told to dial. It covers {Describe(covered)}. No provisioning bundle can be produced for that "
                + "address until the certificate is reissued: Admin -> Printer certificate.");
        }

        // Only when nothing is configured, and that is not a shortcut. A configured name the
        // certificate covers is a working address whatever the interfaces did - homespool.lan resolves
        // to the new lease and the certificate still vouches for the name - so warning about the
        // addresses underneath it would be telling an operator to fix something that works. Detected
        // addresses only matter when detection is what printers were pointed at.
        //
        // Extra addresses are ordinary either way: a VPN, a container bridge, a second interface.
        // Losing every address the certificate names is not - that is the multi-name hedge exhausted.
        if (string.IsNullOrWhiteSpace(configuredHost)
            && current.Count > 0
            && !covered.Any(name => current.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            return new(PrinterCertificateState.AddressesMoved,
                $"This machine's addresses have changed. The printer certificate covers {Describe(covered)}, and this "
                + $"machine now answers on {Describe(current)} - so printers can no longer verify it. Reissue the "
                + "certificate: Admin -> Printer certificate.");
        }

        if (leafExpires.Value - now < LeafExpiryWarning)
        {
            return new(PrinterCertificateState.LeafExpiring,
                $"The printer certificate expires on {leafExpires.Value:yyyy-MM-dd}. Reissuing it costs a proxy reload "
                + "and nothing at the printers, which trust the authority rather than this certificate: Admin -> "
                + "Printer certificate.");
        }

        if (authorityExpires is not null && authorityExpires.Value - now < AuthorityExpiryWarning)
        {
            return new(PrinterCertificateState.AuthorityExpiring,
                $"The printer certificate AUTHORITY expires on {authorityExpires.Value:yyyy-MM-dd}. This one is not a "
                + "button: the authority is each printer's entire trust store and cannot be delivered over the "
                + "network, so replacing it means a USB visit to every printer, and none of them can connect until "
                + "visited. Plan it rather than discover it.");
        }

        return new(PrinterCertificateState.Ok,
            $"The printer certificate covers {Describe(covered)}, valid until {leafExpires.Value:yyyy-MM-dd}.");
    }

    /// <summary>
    /// Names as a person would read them out, because every one of these strings reaches an
    /// administrator's screen unedited.
    /// </summary>
    private static string Describe(IReadOnlyList<string> names) =>
        names.Count == 0 ? "nothing" : string.Join(", ", names);
}
