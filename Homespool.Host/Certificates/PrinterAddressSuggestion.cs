using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Homespool.Host.Certificates;

/// <summary>
/// How confident we are that a suggested address will keep working.
/// </summary>
public enum AddressDurability
{
    /// <summary>Works now and needs no DNS, but is tied to a DHCP lease.</summary>
    UntilTheLeaseMoves,

    /// <summary>Survives a lease change, provided the router registers DHCP names in its own DNS.</summary>
    SurvivesALeaseChange,

    /// <summary>Almost certainly wrong: a container's own address rather than the host's.</summary>
    ProbablyTheContainersOwn,
}

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
    public static IReadOnlyList<PrinterAddressSuggestion> Classify(IEnumerable<IPAddress> addresses, string? hostName)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        List<PrinterAddressSuggestion> suggestions = [];

        if (!string.IsNullOrWhiteSpace(hostName) && !hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            // A name outlives a lease, but only where something resolves it. .local is excluded
            // because that is mDNS, which this firmware does not do - it fails the resolution half
            // rather than the matching half.
            suggestions.Add(new PrinterAddressSuggestion(
                hostName,
                AddressDurability.SurvivesALeaseChange,
                "Survives a change of address, but only if your router publishes names to its own DNS. "
                + "Test it before relying on it."));
        }

        foreach (IPAddress address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                                               .Where(a => !IPAddress.IsLoopback(a))
                                               .Distinct())
        {
            byte[] octets = address.GetAddressBytes();

            if (octets[0] == 169 && octets[1] == 254)
            {
                continue;   // link-local: the machine failed to get an address at all
            }

            bool containerish = octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31;

            suggestions.Add(new PrinterAddressSuggestion(
                address.ToString(),
                containerish ? AddressDurability.ProbablyTheContainersOwn : AddressDurability.UntilTheLeaseMoves,
                containerish
                    ? "This looks like a Docker address, which is this container's own - printers on your "
                      + "network cannot reach it. Use the address of the machine running Docker instead."
                    : "Works immediately and needs no DNS, but stops working if this machine's address "
                      + "changes. A static lease or DHCP reservation avoids that."));
        }

        // Least-likely-to-work last, so the page's first option is its best one.
        return [.. suggestions.OrderBy(s => s.Durability == AddressDurability.ProbablyTheContainersOwn ? 1 : 0)];
    }

    /// <summary>
    /// Reads this machine's usable addresses and name.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Classify"/> so the classification - the part with judgement in it -
    /// can be tested without a network. This half is a straight read of the platform and is not
    /// worth faking.
    /// </remarks>
    public static IReadOnlyList<PrinterAddressSuggestion> Gather()
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

        return Classify(addresses, hostName);
    }
}
