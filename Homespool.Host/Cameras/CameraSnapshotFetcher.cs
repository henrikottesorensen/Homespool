using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
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
    /// A named client rather than the default one because its handler carries the address policy:
    /// <c>Program.cs</c> gives it a connect callback that refuses loopback and link-local at the
    /// endpoint actually being dialled. Sharing the default client would apply that to every
    /// outbound request in the application, and this is the first one there has ever been.
    /// </remarks>
    public const string HttpClientName = "camera";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CameraOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CameraSnapshotFetcher> _logger;

    public CameraSnapshotFetcher(
        IHttpClientFactory httpClientFactory,
        IOptions<CameraOptions> options,
        TimeProvider timeProvider,
        ILogger<CameraSnapshotFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CameraFrame?> FetchAsync(string address, CancellationToken cancellationToken)
    {
        CameraOptions options = _options.Value;

        CameraAddressCheck check = CameraAddressPolicy.Inspect(address);
        if (!check.IsAcceptable)
        {
            _logger.LogWarning("Camera address rejected: {Reason}", check.Error);
            return null;
        }

        Uri uri = check.Uri!;

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

            // An image, or nothing. A camera answering with HTML is usually a login page or an
            // error, and storing those bytes as "the frame" would show a person a rendered web page
            // where they expected their printer.
            if (contentType is null ||
                !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Camera at {Host} answered {ContentType}, which is not an image.",
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

            return new CameraFrame(bytes, contentType, _timeProvider.GetUtcNow());
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
    /// Reads the body, giving up as soon as it exceeds the limit rather than after.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the limit is passed. The read stops at that point, so an
    /// endless response costs the limit and not the stream - which is the whole reason to read
    /// rather than call <c>ReadAsByteArrayAsync</c>, whose own cap cannot be set per client.
    /// </remarks>
    private static async Task<byte[]?> ReadCappedAsync(
        HttpResponseMessage response, long limit, CancellationToken cancellationToken)
    {
        using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
