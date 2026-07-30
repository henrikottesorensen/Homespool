using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Certificates;

/// <summary>
/// Resolves names the way the machine itself would — <c>/etc/hosts</c> first, then DNS.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded, and it has to be.</b> This runs while rendering a page, and an unresolvable name can
/// otherwise sit in the resolver for the platform's own timeout — seconds, on a machine whose DNS is
/// unhappy. A page that hangs while deciding what to offer would be worse than one that offers a name
/// too many.
/// </para>
/// <para>
/// <b>Every failure is "no idea", never "no".</b> A timeout, a refused query and a missing record are
/// indistinguishable from here, and the caller only ever acts on a positive answer — so returning
/// nothing keeps a name in the list rather than dropping it on the strength of a network hiccup.
/// </para>
/// </remarks>
public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    /// <summary>
    /// How long to wait for a name. Short: this is a page render, and the answer is advisory.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        // An address is already an answer; asking a resolver about it invites a reverse lookup and a
        // pointless wait.
        if (IPAddress.TryParse(name, out IPAddress? literal))
        {
            return [literal];
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            return await Dns.GetHostAddressesAsync(name, timeout.Token);
        }
        catch (SocketException)
        {
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            // Not a name the platform will even attempt - too long, or illegal characters.
            return [];
        }
    }
}
