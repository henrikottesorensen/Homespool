using System.Net;

namespace Homespool.Host.Listeners;

/// <summary>An address one of this process's network interfaces holds, with the prefix length of its subnet.</summary>
/// <param name="Address">The address.</param>
/// <param name="PrefixLength">How many leading bits name the subnet the address is on.</param>
public readonly record struct InterfaceAddress(IPAddress Address, int PrefixLength);
