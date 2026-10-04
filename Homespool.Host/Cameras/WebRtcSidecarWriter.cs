using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Homespool.Host.Cameras;

/// <summary>
/// Puts the WebRTC half of the stream server's configuration in place: which address to advertise,
/// and whether it may ask a public STUN server for another one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two callers, one writer.</b> <see cref="WebRtcConfigurer"/> calls this at startup and the
/// live-view settings page calls it when somebody changes the STUN choice. Both halves are written
/// every time, whichever one changed: the sidecar merges a written document key by key but replaces a
/// list outright, so <c>candidates</c> and <c>ice_servers</c> each have to arrive whole - and an
/// empty <c>ice_servers</c> is what switches go2rtc's own public STUN default off.
/// </para>
/// <para>
/// <b>It writes only when the sidecar does not already agree</b>, and that is not an optimisation:
/// applying a change means restarting the sidecar, which drops whoever is watching. A start that
/// changed nothing must not cost somebody their picture.
/// </para>
/// <para>
/// <b>The comparison reads the <c>webrtc</c> section, not the document.</b> The same file holds every
/// camera's source, and a source is a string somebody typed: one containing the candidate or the STUN
/// address must not be able to answer for a section it is not in. A YAML reader covers JSON too, so
/// this holds whichever of the two go2rtc renders.
/// </para>
/// </remarks>
public sealed class WebRtcSidecarWriter
{
    private readonly Go2RtcClient _streamServer;
    private readonly IOptionsMonitor<CameraOptions> _options;
    private readonly ILogger<WebRtcSidecarWriter> _logger;

    public WebRtcSidecarWriter(Go2RtcClient streamServer,
                               IOptionsMonitor<CameraOptions> options,
                               ILogger<WebRtcSidecarWriter> logger)
    {
        _streamServer = streamServer;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Makes the sidecar advertise <paramref name="candidate"/>, with STUN on or off as asked.
    /// Returns whether the sidecar now reflects that.
    /// </summary>
    /// <remarks>
    /// <b>The registered streams are left alone.</b> The write merges into the sidecar's
    /// configuration file and the restart after it reloads the streams from that same file, measured
    /// 2026-09-27. What the write can lose is a stream being registered at the same moment - see
    /// <see cref="Go2RtcClient.WriteConfigAsync"/> - which the startup caller avoids by running before
    /// <see cref="CameraStreamReconciler"/>. The settings page's call has no such ordering: a camera
    /// saved in the same moment can be dropped from the file and lost at the restart.
    /// </remarks>
    public async Task<bool> EnsureAsync(string candidate, bool stunEnabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        string stunServer = _options.CurrentValue.WebRtcStunServer.Trim();

        string? existing = await _streamServer.ReadConfigAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null && Matches(existing, candidate, stunEnabled, stunServer))
        {
            _logger.LogDebug("The stream server already advertises {Candidate}.", candidate);
            return true;
        }

        // Key spellings as data rather than an anonymous type: they are go2rtc's, and ice_servers is
        // not a name C# would have chosen.
        //
        // ice_servers is written EMPTY rather than omitted when STUN is off. Omitting it would leave
        // go2rtc's own default in place, which is to contact a public STUN server unprompted and put
        // this deployment's public address into every offer - so the empty list is what makes "off"
        // mean anything, and writing it every time is what makes turning it back off work.
        string document = JsonSerializer.Serialize(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["webrtc"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["candidates"] = new[] { candidate },
                    ["ice_servers"] = stunEnabled ?
                        new[] { new Dictionary<string, object>(StringComparer.Ordinal) { ["urls"] = new[] { stunServer } } } :
                        [],
                },
            });

        if (!await _streamServer.WriteConfigAsync(document, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Streams apply the moment they are written; a candidate does not, and reads back from the
        // configuration looking applied while being absent from every offer. So the restart is not
        // tidiness - it is what makes the write mean anything.
        if (!await _streamServer.RestartAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _logger.LogInformation(
            "The stream server now advertises {Candidate} for live camera view, with STUN {StunState}.",
            candidate,
            stunEnabled ? "enabled" : "disabled");

        return true;
    }

    /// <summary>
    /// Whether a configuration document's <c>webrtc</c> section already says what we would write.
    /// </summary>
    /// <remarks>
    /// Both lists have to be exactly what a write would put there, since a write replaces them whole.
    /// An <c>ice_servers</c> that is missing is not the same as an empty one: missing leaves go2rtc's
    /// own public STUN default in force. A document that cannot be read says nothing, and is written.
    /// </remarks>
    public static bool Matches(string document, string candidate, bool stunEnabled, string stunServer)
    {
        ArgumentNullException.ThrowIfNull(document);

        YamlStream yaml = new();

        try
        {
            using StringReader reader = new(document);
            yaml.Load(reader);
        }
        catch (YamlException)
        {
            return false;
        }

        if (yaml.Documents.Count == 0 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root ||
            !root.Children.TryGetValue(new YamlScalarNode("webrtc"), out YamlNode? section) ||
            section is not YamlMappingNode webrtc)
        {
            return false;
        }

        if (!webrtc.Children.TryGetValue(new YamlScalarNode("candidates"), out YamlNode? candidates) ||
            !IsList(candidates, candidate) ||
            !webrtc.Children.TryGetValue(new YamlScalarNode("ice_servers"), out YamlNode? iceServers) ||
            iceServers is not YamlSequenceNode servers)
        {
            return false;
        }

        if (!stunEnabled)
        {
            return servers.Children.Count == 0;
        }

        return servers.Children is [YamlMappingNode server] &&
               server.Children.Count == 1 &&
               server.Children.TryGetValue(new YamlScalarNode("urls"), out YamlNode? urls) &&
               IsList(urls, stunServer);
    }

    /// <summary>Whether a node is a list holding exactly one value, and that value is <paramref name="only"/>.</summary>
    private static bool IsList(YamlNode node, string only)
    {
        return node is YamlSequenceNode { Children: [YamlScalarNode { Value: { } value }] } &&
               string.Equals(value, only, StringComparison.Ordinal);
    }
}
