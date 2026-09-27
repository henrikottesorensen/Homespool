using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Homespool.Host.Cameras;

/// <summary>
/// Opens a camera's live MJPEG stream and proves it is producing pictures before anyone commits an
/// answer to a browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>The proof is the point.</b> The stream server writes its 200 before it knows whether it can
/// serve the camera at all — a source it cannot carry answers success and then silence, with the
/// refusal visible only in its own log. So nothing is handed back to the endpoint until one whole
/// multipart part has arrived; a camera that produces nothing within
/// <see cref="FirstFrameTimeout"/> is reported as exactly that.
/// </para>
/// <para>
/// After that first part the stream is passed on as it comes - see <see cref="MjpegFirstPartBuffer"/>.
/// </para>
/// <para>
/// <b>The response's type is ours, not the sidecar's.</b> The outer type is what decides whether a
/// browser opening the stream URL in a tab treats it as a document, so an upstream that is not
/// <c>multipart/x-mixed-replace</c> is refused rather than repeated, and the header sent on is
/// composed here with only the boundary taken from upstream - it has to be, to match the bytes being
/// relayed. The parts' own headers are passed through unread.
/// </para>
/// </remarks>
public sealed class CameraStreamRelay
{
    private const string MultipartMixedReplace = "multipart/x-mixed-replace";

    /// <summary>RFC 2046's limit on a boundary's length.</summary>
    private const int MaxBoundaryLength = 70;

    /// <summary>
    /// How long the stream may stay silent before it is called a failure. Generous against the
    /// measured start-up of a cold USB camera, which spins up an ffmpeg to open the device — and it
    /// only has to be paid when nobody is already watching.
    /// </summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(8);

    private readonly Go2RtcClient _streamServer;
    private readonly ILogger<CameraStreamRelay> _logger;

    public CameraStreamRelay(Go2RtcClient streamServer, ILogger<CameraStreamRelay> logger)
    {
        _streamServer = streamServer;
        _logger = logger;
    }

    /// <summary>
    /// Opens the camera's stream and waits for its first frame. <see langword="null"/> means no
    /// pictures: the sidecar is unusable, refused, or the camera produced nothing in time — all of
    /// which the endpoint answers the same way, because they demand the same thing of the viewer.
    /// </summary>
    public async Task<LiveMjpegStream?> OpenAsync(Guid cameraUuid, CancellationToken cancellationToken)
    {
        HttpResponseMessage? upstream = await _streamServer
                                              .OpenMjpegStreamAsync(cameraUuid, cancellationToken)
                                              .ConfigureAwait(false);

        if (upstream is null)
        {
            return null;
        }

        try
        {
            if (!upstream.IsSuccessStatusCode)
            {
                upstream.Dispose();
                return null;
            }

            // Before the first-frame wait, so a stream that will be refused anyway costs nothing.
            MediaTypeHeaderValue? upstreamType = upstream.Content.Headers.ContentType;
            if (ContentTypeFor(upstreamType) is not { } contentType)
            {
                _logger.LogWarning(
                    "The stream server answered a live stream as {ContentType}, which is not a multipart MJPEG stream.",
                    upstreamType?.ToString() ?? "no content type");
                upstream.Dispose();
                return null;
            }

            Stream body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            MjpegFirstPartBuffer firstPart = new(body);

            using CancellationTokenSource firstFrame =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            firstFrame.CancelAfter(FirstFrameTimeout);

            if (!await firstPart.TryBufferFirstPartAsync(firstFrame.Token).ConfigureAwait(false))
            {
                upstream.Dispose();
                return null;
            }

            return new LiveMjpegStream(upstream, firstPart, contentType);
        }
        catch (Exception exception) when (exception is OperationCanceledException or
                                                       IOException or
                                                       HttpRequestException)
        {
            upstream.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The type to answer with, or <see langword="null"/> when the upstream's is not a multipart
    /// stream with a boundary that can be written back as it stands.
    /// </summary>
    /// <remarks>
    /// The boundary is held to letters, digits and <c>' + _ - .</c> - the characters RFC 2046 allows
    /// in a boundary that also need no quoting in a header, so what is written back is never quoted or
    /// escaped. go2rtc's is <c>frame</c>.
    /// </remarks>
    private static string? ContentTypeFor(MediaTypeHeaderValue? upstream)
    {
        if (upstream is null ||
            !string.Equals(upstream.MediaType, MultipartMixedReplace, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? boundary = upstream.Parameters
                                   .FirstOrDefault(parameter => string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase))
                                   ?.Value;

        // Quoting is the header's syntax, not part of the boundary.
        if (boundary is ['"', .. string quoted, '"'])
        {
            boundary = quoted;
        }

        if (string.IsNullOrEmpty(boundary) ||
            boundary.Length > MaxBoundaryLength ||
            !boundary.All(IsBoundaryCharacter))
        {
            return null;
        }

        return $"{MultipartMixedReplace}; boundary={boundary}";
    }

    private static bool IsBoundaryCharacter(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || c is '\'' or '+' or '_' or '-' or '.';
    }
}

/// <summary>
/// A live MJPEG stream with its first frame already in hand. Dispose to release the camera.
/// </summary>
public sealed class LiveMjpegStream : IDisposable
{
    private readonly HttpResponseMessage _upstream;
    private readonly MjpegFirstPartBuffer _firstPart;

    internal LiveMjpegStream(HttpResponseMessage upstream, MjpegFirstPartBuffer firstPart, string contentType)
    {
        _upstream = upstream;
        _firstPart = firstPart;
        ContentType = contentType;
    }

    /// <summary>The response's content type: <c>multipart/x-mixed-replace</c> and the upstream's checked boundary.</summary>
    public string ContentType { get; }

    /// <summary>
    /// Relays the stream — buffered first frame, then everything after it as it comes. Returns when
    /// the upstream ends; cancelling is how a viewer leaving ends the copy, and
    /// what lets the stream server release the camera.
    /// </summary>
    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        return _firstPart.CopyToAsync(destination, cancellationToken);
    }

    public void Dispose()
    {
        _upstream.Dispose();
    }
}
