using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Options;

using Homespool.Host.Certificates;

namespace Homespool.Host.Cameras;

/// <summary>
/// What a camera source may be, checked before it is handed to the stream server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Homespool never fetches the source itself</b> — go2rtc does, and it will fetch whatever it is
/// given. So this check is not defending our own outbound request; it is deciding what the sidecar
/// is asked to reach on our behalf, which is the same authority wearing a different hat
/// (Henrik, 2026-08-08: <i>"We could sanity check it before handing the url over?"</i>).
/// </para>
/// <para>
/// <b>An allowlist of schemes rather than a denylist.</b> go2rtc understands sources that run
/// programs, and while its HTTP API refuses <c>exec:</c> and <c>echo:</c> outright — measured
/// 2026-08-08, both answer 400 — that is their guard and not ours, and a denylist would silently
/// gain holes as they add sources. Naming what is allowed cannot.
/// </para>
/// <para>
/// <b>The known limit, stated rather than discovered:</b> this runs when the address is saved and
/// the sidecar connects later, so a name that resolves past the check and then elsewhere defeats
/// it. Closing that means pinning the resolved address for the life of the stream, which is not
/// ours to do — the connection belongs to go2rtc.
/// </para>
/// </remarks>
public sealed class CameraSourcePolicy
{
    /// <summary>
    /// The one non-URL source form allowed: a local video device, which has no host to check.
    /// </summary>
    private const string DevicePrefix = "ffmpeg:device?";

    /// <summary>
    /// Whether a source reads hardware attached to this machine rather than something on the
    /// network.
    /// </summary>
    /// <remarks>
    /// The distinction is not cosmetic: a networked camera belongs to whoever can already reach it,
    /// while one plugged into the server is a property of the machine - which is why the two are
    /// permissioned differently. See <c>Capability</c>.
    /// </remarks>
    public static bool IsLocalDevice(string? source)
    {
        return source is not null
               && source.TrimStart().StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Schemes a camera source may use. Everything else is refused.
    /// </summary>
    /// <remarks>
    /// <b><c>onvif</c> is here because go2rtc resolves it to a stream itself</b>, which is the whole
    /// reason the sidecar exists — an ONVIF camera is one address rather than a discovery step the
    /// person adding it has to perform by hand. It names a host like the others, so the
    /// reachability check below applies to it unchanged.
    /// </remarks>
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "rtsp", "rtsps", "http", "https", "rtmp", "onvif" };

    private readonly IHostAddressResolver _resolver;
    private readonly IOptions<CameraOptions> _options;
    private readonly IOptions<CertificateOptions> _certificates;
    private readonly IOptions<PrusaConnect.PrusaConnectOptions> _connect;

    public CameraSourcePolicy(IHostAddressResolver resolver,
                              IOptions<CameraOptions> options,
                              IOptions<CertificateOptions> certificates,
                              IOptions<PrusaConnect.PrusaConnectOptions> connect)
    {
        _resolver = resolver;
        _options = options;
        _certificates = certificates;
        _connect = connect;
    }

    /// <summary>
    /// Whether <paramref name="host"/> is one of the names this deployment answers to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Homespool has two identities and both have to be refused</b> (Henrik, 2026-08-17). Inside
    /// the Compose network it is <c>homespool</c> on 8080 and 15443, beside <c>go2rtc</c> on 1984;
    /// from outside it is whatever address the operator publishes, in front of nginx. A check that
    /// covered only one of the two would refuse the obvious spelling and accept the other.
    /// </para>
    /// <para>
    /// <b>The container half is closed; the outer half is only as good as
    /// <c>PrusaConnect:PrinterHost</c>.</b> That is not an oversight — the application is never told
    /// its user-facing names (<c>USER_HOSTS</c> goes to the proxy alone, and
    /// <c>notes/tls-by-default.md</c> records that nobody stores the answer), so it cannot refuse a
    /// name it has never been given. What that leaves reachable is our own front door over nginx,
    /// answering an unauthenticated fetch with a login page; the sidecar's API, which is the target
    /// that matters, is container-side and fully covered.
    /// </para>
    /// <para>
    /// Deliberately not derived from the request: <c>Host</c> is written by the caller, which is a
    /// separate finding of its own, and a check that trusted it could be told what to permit.
    /// </para>
    /// </remarks>
    public static bool NamesThisDeployment(string? host, IEnumerable<string> deploymentNames)
    {
        ArgumentNullException.ThrowIfNull(deploymentNames);

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string trimmed = host.Trim().TrimEnd('.');

        foreach (string name in deploymentNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string candidate = name.Trim().TrimEnd('.');

            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // "homespool" against "homespool.local", in both directions: a container's short name and
            // the same machine's search-domain form are the same host, and refusing only the spelling
            // we happened to store would be a refusal somebody could step around by typing the other.
            if (candidate.Contains('.', StringComparison.Ordinal)
                && candidate.StartsWith(trimmed + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmed.Contains('.', StringComparison.Ordinal)
                && trimmed.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="address"/> belongs to this deployment rather than to the network
    /// around it.
    /// </summary>
    /// <remarks>
    /// Two sources, because they catch different things. The container networks catch every service
    /// in the stack including one this class has never heard of; this process's own interface
    /// addresses catch the case where there are no container networks configured at all - a rig, or
    /// a deployment on a 172.16/12 LAN that was told to empty the list.
    /// </remarks>
    public static bool IsInsideThisDeployment(IPAddress address,
                                              IReadOnlyList<IPNetwork> containerNetworks,
                                              IReadOnlyList<IPAddress> ownAddresses)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(containerNetworks);
        ArgumentNullException.ThrowIfNull(ownAddresses);

        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        foreach (IPNetwork network in containerNetworks)
        {
            if (network.Contains(candidate))
            {
                return true;
            }
        }

        foreach (IPAddress own in ownAddresses)
        {
            IPAddress mine = own.IsIPv4MappedToIPv6 ? own.MapToIPv4() : own;

            if (mine.Equals(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// This process's own addresses. Loopback is excluded because
    /// <see cref="IsReachableAddress"/> has already refused it, and including it here would only
    /// produce a second, less clear refusal for the same address.
    /// </summary>
    private static IReadOnlyList<IPAddress> OwnAddresses()
    {
        try
        {
            return
            [
                .. System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                         .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                         .Where(nic => nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                         .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                         .Select(unicast => unicast.Address)
            ];
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // Enumeration is a courtesy on top of the container ranges and the names, so a platform
            // that will not answer costs a narrower check rather than a failed save.
            return [];
        }
    }

    /// <summary>
    /// Whether an address may be reached: refuses loopback and link-local, including IPv6 and the
    /// IPv4-mapped forms of both.
    /// </summary>
    /// <remarks>
    /// Everything else is allowed deliberately. Reaching a camera on the LAN is the entire purpose,
    /// so a private-range allowlist would refuse the ordinary case and serve only a deployment shape
    /// this project does not have.
    /// </remarks>
    public static bool IsReachableAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(candidate))
        {
            return false;
        }

        // 0.0.0.0 and ::, which are not loopback and were therefore accepted. On Linux a connection
        // to 0.0.0.0 reaches the local host, so this was a second spelling of loopback that the
        // check above did not cover.
        if (candidate.Equals(IPAddress.Any) || candidate.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (candidate.AddressFamily == AddressFamily.InterNetworkV6 && candidate.IsIPv6LinkLocal)
        {
            return false;
        }

        if (candidate.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] octets = candidate.GetAddressBytes();

            // 169.254.0.0/16 - IPv4 link-local, and where cloud metadata endpoints live.
            if (octets[0] == 169 && octets[1] == 254)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks a camera source: its shape, and where its host resolves to.
    /// </summary>
    /// <summary>
    /// Every name this deployment is known by, as far as this process can tell.
    /// </summary>
    private IReadOnlyList<string> DeploymentNames()
    {
        List<string> names = [];

        // The sidecar, by the address we were configured to reach it on - so a deployment that
        // renamed or re-homed it is covered without this class knowing how.
        if (Uri.TryCreate(_options.Value.StreamServerBaseUrl, UriKind.Absolute, out Uri? streamServer))
        {
            names.Add(streamServer.Host);
        }

        // This container, which in the shipped stack is "homespool".
        try
        {
            names.Add(System.Net.Dns.GetHostName());
        }
        catch (SocketException)
        {
            // A machine that cannot name itself contributes no name, exactly as PrinterAddressSuggestion
            // treats the same failure.
        }

        // The one outer address the application is actually told about.
        if (_connect.Value.IsPrinterAddressConfigured)
        {
            names.Add(_connect.Value.PrinterHost.Trim());
        }

        return names;
    }

    public async Task<CameraSourceCheck> CheckAsync(string? source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return CameraSourceCheck.Refused("Cameras_SourceMissing");
        }

        string trimmed = source.Trim();

        // A local device reaches no network at all, so there is nothing here to check. Whether the
        // path exists is answered by trying it, which the save does immediately afterwards.
        if (trimmed.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return CameraSourceCheck.Accepted;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return CameraSourceCheck.Refused("Cameras_SourceIncomplete");
        }

        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            return CameraSourceCheck.Refused("Cameras_SourceScheme", uri.Scheme);
        }

        if (!_options.Value.RefuseLoopbackAndLinkLocal)
        {
            return CameraSourceCheck.Accepted;
        }

        // Before resolving, because a name is refused on its own terms: "go2rtc" and "homespool"
        // are answers whatever DNS says about them, and inside the Compose network DNS is the only
        // thing that could disagree.
        if (NamesThisDeployment(uri.Host, DeploymentNames()))
        {
            return CameraSourceCheck.Refused("Cameras_SourceIsThisDeployment", uri.Host);
        }

        // An address in the source is already an answer; a name has to be asked about. An
        // unresolvable name is allowed through on purpose - the resolver cannot tell "no such name"
        // from "DNS is unhappy right now", and a camera that cannot be resolved cannot be reached
        // either, so the attempt that follows reports it far more usefully than a refusal here.
        IReadOnlyList<IPAddress> addresses =
            await _resolver.ResolveAsync(uri.Host, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<IPNetwork> containerNetworks = _certificates.Value.ParsedContainerNetworks;
        IReadOnlyList<IPAddress> ownAddresses = OwnAddresses();

        foreach (IPAddress address in addresses)
        {
            if (!IsReachableAddress(address))
            {
                return CameraSourceCheck.Refused("Cameras_SourceIsThisServer", uri.Host, address);
            }

            if (IsInsideThisDeployment(address, containerNetworks, ownAddresses))
            {
                return CameraSourceCheck.Refused("Cameras_SourceIsThisServer", uri.Host, address);
            }
        }

        return CameraSourceCheck.Accepted;
    }
}
