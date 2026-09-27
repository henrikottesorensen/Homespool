using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homespool.Host.Cameras;

/// <inheritdoc />
public sealed class CameraSnapshotFetcher : ICameraSnapshotFetcher
{
    /// <summary>
    /// Name of the <see cref="IHttpClientFactory"/> client configured for cameras.
    /// </summary>
    /// <remarks>
    /// Named rather than default so that camera reads can be configured - timeouts, handler
    /// lifetime - without touching every outbound request the application might grow later. It once
    /// carried an address policy on its handler; that moved to <see cref="CameraSourcePolicy"/> when
    /// Homespool stopped fetching camera sources itself, since the only address this now connects to is
    /// the sidecar the operator configured.
    /// </remarks>
    public const string HttpClientName = "camera";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<CameraOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CameraSnapshotFetcher> _logger;

    public CameraSnapshotFetcher(IHttpClientFactory httpClientFactory,
                                 IOptionsMonitor<CameraOptions> options,
                                 TimeProvider timeProvider,
                                 ILogger<CameraSnapshotFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CameraFrame?> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        CameraOptions options = _options.CurrentValue;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await client
                                                       .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                                                       .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Camera at {Host} answered {StatusCode}.", uri.Host, (int)response.StatusCode);
                return null;
            }

            string? contentType = response.Content.Headers.ContentType?.MediaType;

            // A JPEG, or nothing. The sidecar's frame endpoint only ever produces one, so anything
            // else is a fault - and "any image" is not narrow enough to pass on: image/svg+xml is a
            // document that runs script, in our origin, when somebody opens the frame URL directly.
            if (!string.Equals(contentType, MediaTypeNames.Image.Jpeg, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Camera at {Host} answered {ContentType}, which is not a JPEG.",
                    uri.Host,
                    contentType ?? "no content type");
                return null;
            }

            // Checked before reading where the camera declares a length, and again while reading
            // where it does not - a declared length is a claim, not a guarantee, and a chunked
            // response declares nothing at all.
            long? declared = response.Content.Headers.ContentLength;
            if (declared > options.MaxFrameBytes)
            {
                _logger.LogWarning(
                    "Camera at {Host} declared {Declared} bytes, over the {Limit} limit.",
                    uri.Host,
                    declared,
                    options.MaxFrameBytes);
                return null;
            }

            byte[]? bytes = await ReadCappedAsync(response, options.MaxFrameBytes, timeout.Token)
                .ConfigureAwait(false);

            if (bytes is null)
            {
                _logger.LogWarning(
                    "Camera at {Host} sent more than the {Limit} byte limit.", uri.Host, options.MaxFrameBytes);
                return null;
            }

            // The header is the sidecar's claim; the start-of-image marker is ours to check. Only
            // the first three bytes, as file(1) does: what follows is an APP segment tagged JFIF,
            // Exif or AVI1, or no APP segment at all, and the sidecar passes a camera's own frames
            // through in every one of those shapes.
            if (!IsJpeg(bytes))
            {
                _logger.LogWarning(
                    "Camera at {Host} answered {ContentType} with a body that is not a JPEG.", uri.Host, contentType);
                return null;
            }

            return new CameraFrame(bytes, MediaTypeNames.Image.Jpeg, _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up - shutdown, or the request went away. Not the camera's fault and
            // not worth a warning.
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Camera at {Host} did not answer within {Timeout}s.", uri.Host, options.TimeoutSeconds);
            return null;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                "Camera at {Host} could not be reached: {Message}", uri.Host, exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Whether the bytes open with a JPEG start-of-image marker followed by the start of another marker.
    /// </summary>
    private static bool IsJpeg(ReadOnlySpan<byte> bytes)
    {
        return bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
    }

    /// <summary>
    /// Reads the body, giving up as soon as it exceeds the limit rather than after.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the limit is passed. The read stops at that point, so an
    /// endless response costs the limit and not the stream - which is the whole reason to read
    /// rather than call <c>ReadAsByteArrayAsync</c>, whose own cap cannot be set per client.
    /// </remarks>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response,
                                                       long limit,
                                                       CancellationToken cancellationToken)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream destination = new();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return destination.ToArray();
                }

                if (destination.Length + read > limit)
                {
                    return null;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
