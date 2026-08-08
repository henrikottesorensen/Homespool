using System;
using System.Net;
using System.Net.Sockets;

namespace Homespool.Host.Cameras;

/// <summary>
/// What a camera address is allowed to be. Shared by the edit surface, which refuses a bad one when
/// it is typed, and by the fetcher, which refuses it again at connection time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both checks exist on purpose.</b> The first is the useful one — it tells a person their
/// address is wrong while they are looking at the field. The second is the one that holds, because
/// the first cannot see what a hostname resolves to, and what was checked on Tuesday is not what is
/// connected to today.
/// </para>
/// </remarks>
public static class CameraAddressPolicy
{
    /// <summary>
    /// Checks a camera address for shape: absolute, and HTTP or HTTPS.
    /// </summary>
    /// <remarks>
    /// <c>rtsp://</c> is refused here rather than supported, and that is a design decision rather
    /// than a gap — protocol breadth belongs in the sidecar, which re-serves RTSP, ONVIF and V4L2 as
    /// an HTTP snapshot so the application needs no protocol knowledge at all. The refusal message
    /// says so, because "invalid URL" would send someone looking for a typo.
    /// </remarks>
    public static CameraAddressCheck Inspect(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new CameraAddressCheck(null, "A camera needs an address.");
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? parsed))
        {
            return new CameraAddressCheck(
                null, "That is not a complete address. It should start with http:// or https://.");
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            string error = parsed.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
                ? "Homespool fetches camera images over HTTP. Point a stream server such as go2rtc "
                  + "at the RTSP camera, and give Homespool the snapshot address it serves."
                : $"Cameras are read over HTTP or HTTPS, not {parsed.Scheme}.";

            return new CameraAddressCheck(null, error);
        }

        return new CameraAddressCheck(parsed, null);
    }

    /// <summary>
    /// Whether an address the connection actually resolved to may be reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refuses loopback and link-local, including IPv6 and the IPv4-mapped forms of both, which is
    /// the part a string comparison against "localhost" misses. Everything else is allowed: reaching
    /// a camera on the LAN is the purpose, so a private-range allowlist would refuse the normal case.
    /// </para>
    /// <para>
    /// This is the check that matters, because it runs against the endpoint being connected to
    /// rather than against text somebody typed.
    /// </para>
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

            // 169.254.0.0/16 - IPv4 link-local, which is also where cloud metadata endpoints live.
            if (octets[0] == 169 && octets[1] == 254)
            {
                return false;
            }
        }

        return true;
    }
}
