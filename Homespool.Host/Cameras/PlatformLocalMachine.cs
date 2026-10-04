using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Homespool.Host.Cameras;

/// <summary>
/// <see cref="ILocalMachine"/> as the platform answers it.
/// </summary>
public sealed class PlatformLocalMachine : ILocalMachine
{
    /// <inheritdoc />
    public string? HostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (SocketException)
        {
            // A machine that cannot name itself contributes no name, exactly as PrinterAddressSuggestion
            // treats the same failure.
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> Addresses()
    {
        try
        {
            // Loopback is left out because CameraSourcePolicy.IsReachableAddress has already refused
            // it, and including it here would only produce a second, less clear refusal for the same
            // address.
            return
            [
                .. NetworkInterface.GetAllNetworkInterfaces()
                                   .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                                   .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                                   .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                                   .Select(unicast => unicast.Address)
            ];
        }
        catch (NetworkInformationException)
        {
            // Enumeration is a courtesy on top of the container ranges and the names, so a platform
            // that will not answer costs a narrower check rather than a failed save.
            return [];
        }
    }
}
