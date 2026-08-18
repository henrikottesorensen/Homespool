using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <summary>
/// Puts the WebRTC half of the stream server's configuration in place: which address to advertise,
/// and whether it may ask a public STUN server for another one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two callers, one writer.</b> <see cref="WebRtcConfigurer"/> calls this at startup and the
/// live-view settings page calls it when somebody changes the STUN choice. Both halves have to be
/// written together because the write replaces the document rather than merging into it, so a caller
/// that knew only its own half would silently clear the other.
/// </para>
/// <para>
/// <b>It writes only when the sidecar does not already agree</b>, and that is not an optimisation:
/// applying a change means restarting the sidecar, which drops whoever is watching. A start that
/// changed nothing must not cost somebody their picture.
/// </para>
/// <para>
/// <b>The comparison is done on the document as text.</b> The only two questions are whether it
/// carries this exact candidate and whether it carries this exact STUN address, and a substring
/// answers both — so this works whether go2rtc renders that document as YAML or as JSON, and keeps
/// working if it changes its mind. It is also why the STUN server is a single configured address
/// rather than a list: one string to look for.
/// </para>
/// </remarks>
public sealed class WebRtcSidecarWriter
{
    private readonly Go2RtcClient _streamServer;
    private readonly IOptions<CameraOptions> _options;
    private readonly ILogger<WebRtcSidecarWriter> _logger;

    public WebRtcSidecarWriter(Go2RtcClient streamServer,
                               IOptions<CameraOptions> options,
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
    /// <b>Every registered stream is lost by this and put back by
    /// <see cref="CameraStreamReconciler"/>.</b> Writing replaces the document, measured 2026-08-09
    /// when a write carrying only a <c>webrtc</c> block wiped them — they survived in memory until
    /// the next restart and then vanished. Merging instead would mean reading and rewriting a
    /// document in a format this application does not own; leaning on the reconciler, whose whole
    /// purpose is putting streams back that the sidecar has lost, costs one re-registration each.
    /// The ordering that makes that safe is in <see cref="WebRtcConfigurer"/>.
    /// </remarks>
    public async Task<bool> EnsureAsync(string candidate, bool stunEnabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        string stunServer = _options.Value.WebRtcStunServer.Trim();

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
                    ["ice_servers"] = stunEnabled
                        ? new[] { new Dictionary<string, object>(StringComparer.Ordinal) { ["urls"] = new[] { stunServer } } }
                        : [],
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
    /// Whether a configuration document already says what we would write.
    /// </summary>
    private static bool Matches(string document, string candidate, bool stunEnabled, string stunServer)
    {
        bool hasCandidate = document.Contains(candidate, StringComparison.Ordinal);
        bool hasStun = stunServer.Length > 0 && document.Contains(stunServer, StringComparison.Ordinal);

        return hasCandidate && hasStun == stunEnabled;
    }
}
