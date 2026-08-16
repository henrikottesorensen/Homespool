using System;
using System.Collections.Generic;
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

    public CameraSourcePolicy(IHostAddressResolver resolver, IOptions<CameraOptions> options)
    {
        _resolver = resolver;
        _options = options;
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
    public async Task<CameraSourceCheck> CheckAsync(string? source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return CameraSourceCheck.Refused("A camera needs a source address.");
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
            return CameraSourceCheck.Refused(
                "That is not a complete address. A camera looks like rtsp://192.168.1.50/live or "
                + "http://192.168.1.50/snapshot.jpg.");
        }

        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            return CameraSourceCheck.Refused(
                $"Homespool does not read cameras over {uri.Scheme}. Use rtsp, rtsps, http, https, "
                + "rtmp or onvif.");
        }

        if (!_options.Value.RefuseLoopbackAndLinkLocal)
        {
            return CameraSourceCheck.Accepted;
        }

        // An address in the source is already an answer; a name has to be asked about. An
        // unresolvable name is allowed through on purpose - the resolver cannot tell "no such name"
        // from "DNS is unhappy right now", and a camera that cannot be resolved cannot be reached
        // either, so the attempt that follows reports it far more usefully than a refusal here.
        IReadOnlyList<IPAddress> addresses =
            await _resolver.ResolveAsync(uri.Host, cancellationToken).ConfigureAwait(false);

        foreach (IPAddress address in addresses)
        {
            if (!IsReachableAddress(address))
            {
                return CameraSourceCheck.Refused(
                    $"{uri.Host} resolves to {address}, which is this server itself rather than a "
                    + "camera on your network.");
            }
        }

        return CameraSourceCheck.Accepted;
    }
}
