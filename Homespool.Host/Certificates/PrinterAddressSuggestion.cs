using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Homespool.Host.Certificates;

/// <summary>
/// One candidate address to offer the operator at first-run setup, with what it will cost them.
/// </summary>
/// <param name="Value">The literal to put in the printer's <c>hostname</c> and the certificate's SAN.</param>
/// <param name="Durability">What is likely to break it.</param>
/// <param name="Note">One line of plain English for the setup page.</param>
public record PrinterAddressSuggestion(string Value, AddressDurability Durability, string Note)
{
    /// <summary>
    /// Classifies candidates for the setup page, most-likely-useful first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Suggestions, never a decision.</b> Detection reports what is true now; the question is what
    /// will stay true, and that depends on a router we cannot see. Whatever is chosen automatically
    /// would be a guess wearing the costume of a decision — see <c>notes/tls-by-default.md</c>,
    /// decision 2. This exists to turn an interrogation into a confirmation, nothing more.
    /// </para>
    /// <para>
    /// <b>The trap it must not walk into:</b> the primary deployment is <c>compose.yaml</c>, and inside
    /// a container the detected address is the <i>container's</i>, not the host's. A first-run page
    /// that confidently proposed <c>172.17.0.2</c> would produce a bundle that cannot work and give no
    /// clue why, so that range is surfaced with a warning rather than hidden — hiding it would leave
    /// the page suggesting nothing at all in exactly the deployment that most needs help.
    /// </para>
    /// </remarks>
    /// <param name="addresses">Candidate addresses, typically from <see cref="Gather"/>.</param>
    /// <param name="hostName">The machine's own name, or null if it has none worth offering.</param>
    /// <param name="containerNetworks">
    /// Ranges that exist only inside this deployment, from <see cref="CertificateOptions.ContainerNetworks"/>.
    /// Empty means nothing is treated as container-internal, which is right for a deployment that is not
    /// in one.
    /// </param>
    public static IReadOnlyList<PrinterAddressSuggestion> Classify(IEnumerable<IPAddress> addresses,
                                                                   string? hostName,
                                                                   IReadOnlyList<IPNetwork> containerNetworks)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(containerNetworks);

        List<PrinterAddressSuggestion> suggestions = [];

        if (!string.IsNullOrWhiteSpace(hostName) && !hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            // A name outlives a lease, but only where something resolves it. .local is excluded
            // because that is mDNS, which this firmware does not do - it fails the resolution half
            // rather than the matching half.
            suggestions.Add(Describe(hostName, containerNetworks));
        }

        foreach (IPAddress address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                                               .Where(a => !IPAddress.IsLoopback(a))
                                               .Distinct())
        {
            byte[] octets = address.GetAddressBytes();

            if (octets[0] == 169 && octets[1] == 254)
            {
                continue; // link-local: the machine failed to get an address at all
            }

            suggestions.Add(Describe(address.ToString(), containerNetworks));
        }

        // Least-likely-to-work last, so the page's first option is its best one.
        return [.. suggestions.OrderBy(s => s.Durability == AddressDurability.ProbablyTheContainersOwn ? 1 : 0)];
    }

    /// <summary>
    /// What this one name costs whoever picks it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Classify"/> because the two callers arrive from opposite directions:
    /// classification starts from what the machine can see, while the provisioning page starts from
    /// what the <i>certificate</i> already covers and needs the same sentence about a name it did not
    /// discover. Both end up here, so the wording exists once.
    /// </para>
    /// <para>
    /// <b>Judged by shape, not by resolution</b>, and only ever to describe: whether a name can be
    /// reached is a different question, answered by asking the resolver
    /// (<c>ProvisioningBundleBuilder.IsUnreachableByPrinters</c>). This says what it will cost you if
    /// it can.
    /// </para>
    /// </remarks>
    /// <param name="value">A hostname or an address, as it would be written into a printer's ini.</param>
    /// <param name="containerNetworks">The deployment's own internal ranges.</param>
    public static PrinterAddressSuggestion Describe(string value, IReadOnlyList<IPNetwork> containerNetworks)
    {
        ArgumentNullException.ThrowIfNull(containerNetworks);

        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            return new PrinterAddressSuggestion(
                value,
                AddressDurability.SurvivesALeaseChange,
                "Survives a change of address, but only if your router publishes names to its own DNS. "
                + "Test it before relying on it.");
        }

        return IsProbablyTheContainersOwn(address, containerNetworks) ?
            new PrinterAddressSuggestion(
                value,
                AddressDurability.ProbablyTheContainersOwn,
                "This looks like a Docker address, which is this container's own - printers on your "
                + "network cannot reach it. Use the address of the machine running Docker instead.") :
            new PrinterAddressSuggestion(
                value,
                AddressDurability.UntilTheLeaseMoves,
                "Works immediately and needs no DNS, but stops working if this machine's address "
                + "changes. A static lease or DHCP reservation avoids that.");
    }

    /// <summary>
    /// Whether an address is one a container gave itself, and therefore useless to a printer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ranges the deployment says are its own - Docker's <c>172.16/12</c> by default, and whatever
    /// <c>compose.yaml</c> pins in a stack that changed it. A printer is a physical device on the
    /// household LAN and cannot route to those at all: the address works perfectly from inside the
    /// container and nowhere else, which is what makes it such a convincing wrong answer.
    /// </para>
    /// <para>
    /// Exact rather than heuristic, and told rather than guessed - which is why it is worth having as
    /// its own rule. The name-based version of this question - "is <c>71e04654da9b</c> a Docker
    /// hostname?" - cannot be answered by looking at it, and is answered by resolving it instead
    /// (<c>ProvisioningBundleBuilder.IsUnreachableByPrinters</c>).
    /// </para>
    /// </remarks>
    /// <param name="address">Any address.</param>
    /// <param name="containerNetworks">The deployment's own internal ranges; empty means it has none.</param>
    public static bool IsProbablyTheContainersOwn(IPAddress address, IReadOnlyList<IPNetwork> containerNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(containerNetworks);

        return address.AddressFamily == AddressFamily.InterNetwork
               && containerNetworks.Any(network => network.Contains(address));
    }

    /// <summary>
    /// Reads this machine's usable addresses and name.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Classify"/> so the classification - the part with judgement in it -
    /// can be tested without a network. This half is a straight read of the platform and is not
    /// worth faking.
    /// </remarks>
    /// <param name="containerNetworks">Passed through to <see cref="Classify"/>.</param>
    public static IReadOnlyList<PrinterAddressSuggestion> Gather(IReadOnlyList<IPNetwork> containerNetworks)
    {
        IEnumerable<IPAddress> addresses = NetworkInterface.GetAllNetworkInterfaces()
                                                           .Where(n => n.OperationalStatus == OperationalStatus.Up)
                                                           .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                                                           .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                                                           .Select(u => u.Address);

        string? hostName = null;

        try
        {
            hostName = Dns.GetHostName();
        }
        catch (SocketException)
        {
            // A machine that cannot name itself simply gets no name suggested.
        }

        return Classify(addresses, hostName, containerNetworks);
    }
}
