using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
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

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CameraOptions> _options;
    private readonly ILogger<Go2RtcClient> _logger;

    public Go2RtcClient(IHttpClientFactory httpClientFactory,
                        IOptions<CameraOptions> options,
                        ILogger<Go2RtcClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Where a camera's still image is served from.
    /// </summary>
    /// <remarks>
    /// Derived, never stored. A snapshot address is entirely a function of the sidecar's base
    /// address and the stream name, so persisting it would only create something able to disagree
    /// with both.
    /// </remarks>
    public Uri FrameUrl(Guid streamName)
    {
        return new Uri(
            $"{BaseAddress().TrimEnd('/')}/api/frame.jpeg?src={streamName.ToString("D", CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Registers or replaces a stream. Returns false if the sidecar refused or could not be reached.
    /// </summary>
    public async Task<bool> PutStreamAsync(Guid streamName, string source, CancellationToken cancellationToken)
    {
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

    private string BaseAddress()
    {
        return _options.Value.StreamServerBaseUrl;
    }
}
