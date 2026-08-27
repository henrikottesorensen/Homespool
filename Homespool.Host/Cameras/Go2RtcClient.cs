using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mime;
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
public sealed class Go2RtcClient : ICameraCodecProbe
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
    /// The port the sidecar's RTSP side listens on. go2rtc's default, and Homespool owns the
    /// sidecar's configuration, so nothing moves it.
    /// </summary>
    private const int RtspPort = 8554;

    /// <summary>
    /// How often to say that the sidecar has no credential. Once a minute, because the frame endpoint
    /// is asked every couple of seconds by any open camera page.
    /// </summary>
    private static readonly TimeSpan UncredentialedWarningInterval = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<CameraOptions> _options;
    private readonly ILogger<Go2RtcClient> _logger;
    private readonly Services.LogThrottle _uncredentialed = new(UncredentialedWarningInterval);

    public Go2RtcClient(IHttpClientFactory httpClientFactory,
                        IOptionsMonitor<CameraOptions> options,
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
        if (_options.CurrentValue.IsAuthenticated)
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
    /// Where a camera's continuous MJPEG stream (<c>multipart/x-mixed-replace</c>) is served from, or
    /// <see langword="null"/> when the sidecar has no credential and is therefore not used.
    /// </summary>
    /// <remarks>
    /// Same derivation rule as <see cref="FrameUrl"/>: a function of the base address and the stream
    /// name, never stored. The path must be on the sidecar's <c>allow_paths</c> list in
    /// <c>compose.yaml</c>, which enforces by not registering the handler - a path left off answers
    /// a bare 404 that looks exactly like a wrong address.
    /// </remarks>
    public Uri? MjpegStreamUrl(Guid streamName)
    {
        if (!IsUsable())
        {
            return null;
        }

        return new Uri($"{BaseAddress().TrimEnd('/')}/api/stream.mjpeg?src={streamName.ToString("D", CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Opens a camera's MJPEG stream. The caller owns the response and must dispose it; the body is
    /// unbounded and is read as it arrives.
    /// </summary>
    /// <remarks>
    /// The named client's default 100-second timeout would cut a stream off mid-watch, so it is
    /// lifted on this instance only; the caller's token is what ends the read.
    /// </remarks>
    public async Task<HttpResponseMessage?> OpenMjpegStreamAsync(Guid streamName, CancellationToken cancellationToken)
    {
        if (MjpegStreamUrl(streamName) is not { } url)
        {
            return null;
        }

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;

        try
        {
            return await client
                         .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                         .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("The stream server's MJPEG stream could not be opened: {Message}", exception.Message);
            return null;
        }
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
    /// The MJPEG capture sizes each attached camera offers, keyed by its <c>/dev/videoN</c> node, or
    /// <see langword="null"/> when the stream server could not be asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked of the sidecar because only it can answer.</b> Homespool lists camera *names* from a
    /// read-only mount and deliberately holds no capability to open one (see
    /// <see cref="LocalCameraDevices"/>), so enumerating formats has to happen where the devices
    /// actually are. <c>/api/ffmpeg/devices</c> runs <c>ffmpeg -list_formats</c> across every
    /// <c>/dev/video*</c> and reports each format's sizes in its <c>info</c> field.
    /// </para>
    /// <para>
    /// <b>MJPEG entries only.</b> Every device reports its raw format too, and taking that would
    /// undo the reason <see cref="LocalCameraDevices.SourceFor"/> states <c>input_format=mjpeg</c>
    /// in the first place - a transcode per frame, invisibly.
    /// </para>
    /// <para>
    /// <b>The sizes come back unsorted and sometimes duplicated</b> - measured on the board
    /// 2026-08-20, where one camera listed <c>640x480</c> first and another repeated
    /// <c>1280x720</c>. They are ordered by pixel count and de-duplicated here rather than in a
    /// view, because every reader wants the same thing and none of them wants the camera's order.
    /// </para>
    /// <para>
    /// <b>The sidecar caches this for the life of its process</b>, so a camera plugged in afterwards
    /// is absent until it restarts. That is what the rescan button on the cameras page is for; there
    /// is no cheaper way, and pretending otherwise would leave somebody staring at a list that
    /// cannot include what they just plugged in.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> ListDeviceFormatsAsync(
        CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return null;
        }

        Uri request = new($"{BaseAddress().TrimEnd('/')}/api/ffmpeg/devices");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client.GetAsync(request, cancellationToken)
                                                             .ConfigureAwait(false);

            // "no sources" is a 404 and means this machine has no cameras attached, which is the
            // ordinary case and not a failure - it must not read as "could not be asked".
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The stream server would not list devices: {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            DeviceListing? listing = await response.Content
                                                   .ReadFromJsonAsync<DeviceListing>(cancellationToken)
                                                   .ConfigureAwait(false);

            return ParseDeviceFormats(listing?.Sources);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug("The stream server's devices could not be listed: {Message}", exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Turns the sidecar's device listing into sizes by node, keeping MJPEG only.
    /// </summary>
    /// <remarks>
    /// Public and static so the parse is testable without a sidecar; the shapes it reads are pinned
    /// by the tests against strings captured from the real thing.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseDeviceFormats(
        IReadOnlyList<DeviceSource>? sources)
    {
        Dictionary<string, IReadOnlyList<string>> byNode = new(StringComparer.Ordinal);

        foreach (DeviceSource source in sources ?? [])
        {
            if (source.Url is null || source.Info is null || !source.Url.Contains("input_format=mjpeg", StringComparison.Ordinal))
            {
                continue;
            }

            if (NodeFromUrl(source.Url) is not { } node)
            {
                continue;
            }

            List<string> sizes = source.Info
                                       .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .Where(IsSize)
                                       .Distinct(StringComparer.Ordinal)
                                       .OrderBy(Pixels)
                                       .ToList();

            if (sizes.Count > 0)
            {
                byNode[node] = sizes;
            }
        }

        return byNode;
    }

    /// <summary>The <c>video=</c> parameter of a device URL, as a bare node name.</summary>
    private static string? NodeFromUrl(string url)
    {
        const string Marker = "video=/dev/";

        int at = url.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        int start = at + Marker.Length;
        int end = url.IndexOf('&', start);

        return end < 0 ? url[start..] : url[start..end];
    }

    /// <summary><c>WIDTHxHEIGHT</c>, and nothing else - the info field is free text.</summary>
    private static bool IsSize(string value)
    {
        string[] parts = value.Split('x');

        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
               && w > 0 && h > 0;
    }

    private static long Pixels(string size)
    {
        string[] parts = size.Split('x');

        return long.Parse(parts[0], CultureInfo.InvariantCulture)
               * long.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    /// <summary>One entry of the stream server's device listing.</summary>
    /// <param name="Name">The format's human name, such as <c>Motion-JPEG</c>.</param>
    /// <param name="Info">Space-separated capture sizes, in the camera's own order.</param>
    /// <param name="Url">A composed source for the device, which is where the node name is read from.</param>
    public sealed record DeviceSource(string? Name, string? Info, string? Url);

    /// <summary>The envelope <c>/api/ffmpeg/devices</c> answers with.</summary>
    public sealed record DeviceListing(IReadOnlyList<DeviceSource>? Sources);

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

            using StringContent content = new(document, Encoding.UTF8, MediaTypeNames.Application.Json);
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

    /// <summary>
    /// Longest a codec probe may take, connection included. An RTSP DESCRIBE makes the sidecar open
    /// the camera, and a cold USB camera spins up an ffmpeg first - measured ~0.6s; an RTSP camera
    /// answers a connect in well under a second. Anything slower is a camera that is not answering.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Asked over RTSP, because the HTTP API will not say.</b> Measured (Pi 3, 1.9.14,
    /// 2026-08-18 and again 2026-08-20): <c>/api/streams</c> names a camera's codecs only while
    /// some consumer holds the stream open, and forgets them the moment it closes. A DESCRIBE is
    /// itself the consumer: the sidecar connects the camera to build the SDP, answers with its
    /// codecs, and releases it when the connection drops - one question, one answer, any codec.
    /// </para>
    /// <para>
    /// <b>The RTSP port carries no credential</b>, unlike the HTTP API. That is acceptable for the
    /// same reason it is unavoidable: the port is not published outside the Compose network, so the
    /// only thing that can ask is something already inside - and the gate below keeps the rule that
    /// an uncredentialed sidecar is not used at all.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlySet<string>?> ProbeCodecsAsync(Guid streamName, CancellationToken cancellationToken)
    {
        if (!IsUsable())
        {
            return null;
        }

        Uri baseAddress = new(BaseAddress());
        string name = streamName.ToString("D", CultureInfo.InvariantCulture);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);

        try
        {
            using System.Net.Sockets.TcpClient tcp = new();
            await tcp.ConnectAsync(baseAddress.Host, RtspPort, deadline.Token).ConfigureAwait(false);

            System.IO.Stream wire = tcp.GetStream();

            byte[] request = Encoding.ASCII.GetBytes(
                $"DESCRIBE rtsp://{baseAddress.Host}:{RtspPort}/{name} RTSP/1.0\r\n"
                + "CSeq: 1\r\n"
                + "Accept: application/sdp\r\n"
                + "User-Agent: Homespool\r\n\r\n");

            await wire.WriteAsync(request, deadline.Token).ConfigureAwait(false);

            string answer = await ReadRtspAnswerAsync(wire, deadline.Token).ConfigureAwait(false);

            if (!answer.StartsWith("RTSP/1.0 200", StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "The stream server refused a codec probe for {StreamName}: {StatusLine}.",
                    streamName,
                    answer.Split('\r')[0]);

                return null;
            }

            return ParseSdpVideoCodecs(answer);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException
                                                    or System.IO.IOException
                                                    or OperationCanceledException)
        {
            // The camera is off, unreachable, or slower than any working camera - all of which mean
            // "no answer today", which the caller must not remember as "no".
            _logger.LogDebug("Codec probe for {StreamName} got no answer: {Message}", streamName, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// The video codecs named by an SDP document, upper-cased as the SDP writes them
    /// (<c>H264</c>, <c>JPEG</c>, ...). Audio sections are ignored; a live view is a picture.
    /// </summary>
    /// <remarks>
    /// Public and static so the parse is testable without a socket; the SDP grammar it relies on is
    /// two lines - <c>m=</c> opens a media section, <c>a=rtpmap:&lt;pt&gt; &lt;codec&gt;/&lt;clock&gt;</c>
    /// names the codec - and both are RFC 8866, not go2rtc.
    /// </remarks>
    public static IReadOnlySet<string> ParseSdpVideoCodecs(string sdp)
    {
        HashSet<string> codecs = new(StringComparer.OrdinalIgnoreCase);
        bool inVideo = false;

        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                inVideo = line.StartsWith("m=video", StringComparison.Ordinal);
            }
            else if (inVideo && line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                // "a=rtpmap:96 H264/90000" - the codec sits between the first space and the slash.
                int space = line.IndexOf(' ', StringComparison.Ordinal);
                int slash = line.IndexOf('/', StringComparison.Ordinal);

                if (space > 0 && slash > space)
                {
                    codecs.Add(line[(space + 1)..slash]);
                }
            }
        }

        return codecs;
    }

    /// <summary>
    /// Reads an RTSP response - status line, headers, and as much body as <c>Content-Length</c>
    /// promises - as one string. RTSP frames exactly like HTTP/1.0, which is what makes this small.
    /// </summary>
    private static async Task<string> ReadRtspAnswerAsync(System.IO.Stream wire, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        int length = 0;

        while (true)
        {
            int read = await wire
                             .ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)
                             .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            length += read;

            string text = Encoding.ASCII.GetString(buffer, 0, length);
            int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (headerEnd < 0)
            {
                if (length == buffer.Length)
                {
                    break; // not an RTSP answer; give back what there is and let the caller refuse it
                }

                continue;
            }

            int promised = 0;
            foreach (string line in text[..headerEnd].Split('\n'))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(line["Content-Length:".Length..].Trim(), NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out promised);
                }
            }

            if (length >= headerEnd + 4 + promised || length == buffer.Length)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    private string BaseAddress()
    {
        return _options.CurrentValue.StreamServerBaseUrl;
    }
}
