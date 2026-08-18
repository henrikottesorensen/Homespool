using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <summary>
/// Talks to the go2rtc sidecar: registers a camera's source, removes it, and says where its frames
/// are served from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Homespool owns the sidecar's configuration</b> (Henrik, 2026-08-08). The alternative was an
/// operator editing <c>go2rtc.yaml</c> by hand and then pasting the resulting snapshot address back
/// into Homespool - one camera described in two places, which is the trap <c>compose.yaml</c>
/// already names about settings that answer the same question twice.
/// </para>
/// <para>
/// <b>go2rtc persists what it is told</b>, measured 2026-08-08: a stream added over the API is
/// written into its config file and survives a restart. So there is no shadow copy to keep in step
/// and no write-back to schedule; the reconciler exists for the case where its volume is lost, not
/// for ordinary restarts.
/// </para>
/// </remarks>
public sealed class Go2RtcClient
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for the sidecar.</summary>
    public const string HttpClientName = "go2rtc";

    /// <summary>
    /// A refusal on codec grounds, as the sidecar words it.
    /// </summary>
    /// <remarks>
    /// <b>The body, not the status, and that is measured rather than chosen</b> (Pi 3, 1.9.14,
    /// 2026-08-18). A JPEG camera answers <c>500 streams: codecs not matched: video:JPEG =&gt;
    /// video:H264</c> — and a camera that is merely unplugged answers 500 as well, so the status
    /// cannot separate "this will never work" from "this is not working today". The same shape as
    /// the <c>allow_paths</c> finding, where Go's mux and go2rtc's own handler both answered 404 and
    /// only the body said which.
    /// </remarks>
    private const string CodecsNotMatched = "codecs not matched";

    /// <summary>
    /// How often to say that the sidecar has no credential. Once a minute, because the frame endpoint
    /// is asked every couple of seconds by any open camera page.
    /// </summary>
    private static readonly TimeSpan UncredentialedWarningInterval = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CameraOptions> _options;
    private readonly ILogger<Go2RtcClient> _logger;
    private readonly Services.LogThrottle _uncredentialed = new(UncredentialedWarningInterval);

    public Go2RtcClient(IHttpClientFactory httpClientFactory,
                        IOptions<CameraOptions> options,
                        ILogger<Go2RtcClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Whether this deployment will talk to the sidecar at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The single chokepoint, and deliberately here rather than at the call sites.</b> Five call
    /// sites across four types reach the sidecar, and all of them go through this class - so a rule
    /// enforced there would be five places to forget it, and the sixth caller would be the one that
    /// mattered. Every public member below refuses when this is false.
    /// </para>
    /// <para>
    /// The reasoning for refusing rather than proceeding without a header is in
    /// <see cref="CameraOptions.IsAuthenticated"/>. In short: an uncredentialed sidecar can be driven
    /// into running commands through its own API, and a camera source is how a team member would
    /// reach it.
    /// </para>
    /// </remarks>
    private bool IsUsable()
    {
        if (_options.Value.IsAuthenticated)
        {
            return true;
        }

        if (_uncredentialed.Record() is { } window)
        {
            _logger.LogWarning(
                "Cameras are disabled because the stream server has no credential: set Cameras:ApiUsername and "
                + "Cameras:ApiPassword (GO2RTC_USERNAME and GO2RTC_PASSWORD in .env, which ./setup-env.sh will "
                + "generate). {Count} camera operation(s) refused in the last {Elapsed}, {Total} in total.",
                window.Count,
                window.Elapsed,
                window.Total);
        }

        return false;
    }

    /// <summary>
    /// Where a camera's still image is served from, or <see langword="null"/> when the sidecar has no
    /// credential and is therefore not used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived, never stored. A snapshot address is entirely a function of the sidecar's base
    /// address and the stream name, so persisting it would only create something able to disagree
    /// with both.
    /// </para>
    /// <para>
    /// <b>Nullable so the compiler finds both callers</b>, rather than returning an address nobody
    /// should fetch. A null reads as "no picture available", which is the answer the frame endpoint
    /// and the save path already know how to give for a camera that is merely switched off.
    /// </para>
    /// </remarks>
    public Uri? FrameUrl(Guid streamName)
    {
        if (!IsUsable())
        {
            return null;
        }

        return new Uri(
            $"{BaseAddress().TrimEnd('/')}/api/frame.jpeg?src={streamName.ToString("D", CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Registers or replaces a stream. Returns false if the sidecar refused or could not be reached.
    /// </summary>
    public async Task<bool> PutStreamAsync(Guid streamName, string source, CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return false;
        }

        Uri request = new(
            $"{BaseAddress().TrimEnd('/')}/api/streams"
            + $"?name={Uri.EscapeDataString(streamName.ToString("D", CultureInfo.InvariantCulture))}"
            + $"&src={Uri.EscapeDataString(source)}");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client
                                                       .PutAsync(request, content: null, cancellationToken)
                                                       .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // The sidecar refuses sources it will not serve - exec: and echo: both answer 400,
            // measured 2026-08-08 - so a rejection here is information rather than a fault.
            _logger.LogWarning(
                "The stream server refused camera {Stream}: {StatusCode}.",
                streamName,
                (int)response.StatusCode);

            return false;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "The stream server could not be reached to register camera {Stream}: {Message}",
                streamName,
                exception.Message);

            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The stream server timed out registering camera {Stream}.", streamName);
            return false;
        }
    }

    /// <summary>
    /// Removes a stream. Failure is logged and swallowed: a camera the user deleted is gone from
    /// Homespool either way, and a stream left behind in the sidecar is swept by the reconciler.
    /// </summary>
    public async Task DeleteStreamAsync(Guid streamName, CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return;
        }

        // src, not name. Both are accepted and both answer 200; only src actually removes the
        // stream - measured 2026-08-08, after a delete that reported success left the stream in
        // place. For a camera attached to this machine that is worse than untidy: Homespool would
        // consider the device free and offer it in the picker again while the stream server still
        // held it.
        Uri request = new(
            $"{BaseAddress().TrimEnd('/')}/api/streams"
            + $"?src={Uri.EscapeDataString(streamName.ToString("D", CultureInfo.InvariantCulture))}");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client
                                                       .DeleteAsync(request, cancellationToken)
                                                       .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The stream server would not remove camera {Stream}: {StatusCode}.",
                    streamName,
                    (int)response.StatusCode);
            }
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "The stream server could not be reached to remove camera {Stream}: {Message}",
                streamName,
                exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The stream server timed out removing camera {Stream}.", streamName);
        }
    }

    /// <summary>
    /// The stream names the sidecar currently knows, or <see langword="null"/> if it could not be
    /// asked.
    /// </summary>
    /// <remarks>
    /// Null rather than empty on failure, and the distinction matters: the reconciler would read an
    /// empty set as "everything is missing" and re-register every camera against a sidecar that is
    /// merely still starting.
    /// </remarks>
    public async Task<IReadOnlySet<string>?> ListStreamNamesAsync(CancellationToken cancellationToken)
    {
        // Null, which the reconciler already reads as "could not be asked" and answers by doing
        // nothing. That is exactly right here: with no credential there is nothing to reconcile
        // towards, and the empty set would instead mean "everything is missing".
        if (!IsUsable())
        {
            return null;
        }

        Uri request = new($"{BaseAddress().TrimEnd('/')}/api/streams");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            Dictionary<string, JsonElement>? streams = await client
                                                             .GetFromJsonAsync<Dictionary<string, JsonElement>>(
                                                                 request, cancellationToken)
                                                             .ConfigureAwait(false);

            return streams is null ? new HashSet<string>(StringComparer.Ordinal) : streams.Keys.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug("The stream server could not be listed: {Message}", exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Exchanges a browser's WebRTC offer for the sidecar's answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Signalling only — no picture travels through here.</b> The media goes straight from the
    /// sidecar to the browser over its own published port, which is why that port exists at all.
    /// What this exchange does is hand the browser the ICE credentials it needs to receive any, so
    /// it is the point at which the camera's permission check is spent: see
    /// <c>CameraController.WebRtc</c>, which is the only caller.
    /// </para>
    /// <para>
    /// <b>The offer is passed through unread.</b> It comes from a browser and goes to the sidecar,
    /// and Homespool has no opinion about its contents — parsing it here would only create a second
    /// place able to disagree with the two ends that actually negotiate.
    /// </para>
    /// <para>
    /// <b>The contract is measured, not assumed</b> (Pi 3, 1.9.14, 2026-08-18):
    /// <c>POST /api/webrtc?src=NAME</c> with <c>{"type":"offer","sdp":…}</c> answers <c>200</c> and
    /// <c>{"type":"answer","sdp":…}</c>. A raw SDP body is also accepted and answers <c>201</c> with
    /// <c>application/sdp</c>; that is the WHEP shape and is not what this uses.
    /// </para>
    /// </remarks>
    public async Task<WebRtcOffer> OfferAsync(Guid streamName, string offerSdp, CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return new WebRtcOffer(WebRtcOfferOutcome.Failed, null);
        }

        Uri request = new(
            $"{BaseAddress().TrimEnd('/')}/api/webrtc"
            + $"?src={Uri.EscapeDataString(streamName.ToString("D", CultureInfo.InvariantCulture))}");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client
                                                       .PostAsJsonAsync(
                                                           request,
                                                           new WebRtcDescription("offer", offerSdp),
                                                           cancellationToken)
                                                       .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (body.Contains(CodecsNotMatched, StringComparison.OrdinalIgnoreCase))
                {
                    // Not a fault. This camera sends JPEG, which is a permanent fact about it until
                    // somebody puts different hardware at the same address - so the caller stops
                    // offering live view for it rather than letting it be tried again and again.
                    _logger.LogInformation(
                        "Camera {Stream} cannot be watched live: {Reason}",
                        streamName,
                        body.Trim());

                    return new WebRtcOffer(WebRtcOfferOutcome.CodecUnsupported, null);
                }

                _logger.LogWarning(
                    "The stream server refused a WebRTC offer for camera {Stream}: {StatusCode} {Body}",
                    streamName,
                    (int)response.StatusCode,
                    body.Trim());

                return new WebRtcOffer(WebRtcOfferOutcome.Failed, null);
            }

            WebRtcDescription? answer = await response.Content
                                                      .ReadFromJsonAsync<WebRtcDescription>(cancellationToken)
                                                      .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(answer?.Sdp)
                ? new WebRtcOffer(WebRtcOfferOutcome.Failed, null)
                : new WebRtcOffer(WebRtcOfferOutcome.Answered, answer.Sdp);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _logger.LogWarning(
                "The stream server could not be reached to answer a WebRTC offer for camera {Stream}: {Message}",
                streamName,
                exception.Message);

            return new WebRtcOffer(WebRtcOfferOutcome.Failed, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The stream server timed out answering a WebRTC offer for camera {Stream}.", streamName);
            return new WebRtcOffer(WebRtcOfferOutcome.Failed, null);
        }
    }

    /// <summary>
    /// The sidecar's configuration document as text, or <see langword="null"/> if it could not be
    /// read.
    /// </summary>
    /// <remarks>
    /// <b>Text, deliberately, and never parsed.</b> The one question asked of it is whether it
    /// already contains a particular candidate address, which a substring answers — so this works
    /// whether the sidecar renders that document as YAML or as JSON, and keeps working if it changes
    /// its mind. Parsing it would buy nothing and would be a second place that has to be right about
    /// a format nobody here owns.
    /// </remarks>
    public async Task<string?> ReadConfigAsync(CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return null;
        }

        Uri request = new($"{BaseAddress().TrimEnd('/')}/api/config");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client.GetAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "The stream server would not hand over its configuration: {StatusCode}.",
                    (int)response.StatusCode);

                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug("The stream server's configuration could not be read: {Message}", exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes a configuration document to the sidecar. Returns false if it refused or could not be
    /// reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This REPLACES the document rather than merging into it</b>, measured 2026-08-09 — a PATCH
    /// carrying only a <c>webrtc</c> block wiped every registered stream, which survived in memory
    /// until the next restart and then vanished. So the caller must expect to lose the streams and
    /// have a plan for them; <see cref="WebRtcConfigurer"/> runs before
    /// <see cref="CameraStreamReconciler"/> for exactly that reason, which turns the replacement
    /// from a hazard into the ordinary path.
    /// </para>
    /// <para>
    /// What is <i>not</i> lost is the credential and the path allowlist: both are passed on the
    /// sidecar's command line and were deliberately never written to this file.
    /// </para>
    /// </remarks>
    public async Task<bool> WriteConfigAsync(string document, CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return false;
        }

        Uri request = new($"{BaseAddress().TrimEnd('/')}/api/config");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using StringContent content = new(document, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client
                                                       .PatchAsync(request, content, cancellationToken)
                                                       .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "The stream server refused its new configuration: {StatusCode}.",
                (int)response.StatusCode);

            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("The stream server's configuration could not be written: {Message}", exception.Message);
            return false;
        }
    }

    /// <summary>
    /// Restarts the sidecar. Returns false if it refused or could not be reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A restart is what makes a changed address take effect, and only that.</b> Streams apply
    /// the moment they are written; candidates do not — measured 2026-08-09, and easy to test
    /// wrongly, because a candidate written and not restarted reads back correctly from the
    /// configuration while being absent from every offer.
    /// </para>
    /// <para>
    /// <b>It drops live viewers</b>, briefly but really. That is why the only caller does this at
    /// startup and only when the address has actually changed, and never on a timer.
    /// </para>
    /// <para>
    /// POST is the method that works: GET answers 400 and PUT drops the connection.
    /// </para>
    /// </remarks>
    public async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return false;
        }

        Uri request = new($"{BaseAddress().TrimEnd('/')}/api/restart");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client
                                                       .PostAsync(request, content: null, cancellationToken)
                                                       .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning("The stream server would not restart: {StatusCode}.", (int)response.StatusCode);
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // A sidecar that closes the connection while restarting is doing what was asked, so this
            // is not evidence of failure - only of not having been told it succeeded.
            _logger.LogDebug("The stream server did not answer a restart cleanly: {Message}", exception.Message);
            return false;
        }
    }

    private string BaseAddress()
    {
        return _options.Value.StreamServerBaseUrl;
    }
}
