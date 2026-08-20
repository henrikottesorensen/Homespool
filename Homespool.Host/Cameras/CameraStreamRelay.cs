using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
/// The stream handed back repairs frames as they pass — see <see cref="MjpegDhtRelay"/> for why a
/// USB camera's frames need it.
/// </para>
/// </remarks>
public sealed class CameraStreamRelay
{
    /// <summary>
    /// How long the stream may stay silent before it is called a failure. Generous against the
    /// measured start-up of a cold USB camera, which spins up an ffmpeg to open the device — and it
    /// only has to be paid when nobody is already watching.
    /// </summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(8);

    private readonly Go2RtcClient _streamServer;

    public CameraStreamRelay(Go2RtcClient streamServer)
    {
        _streamServer = streamServer;
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

            Stream body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            MjpegDhtRelay relay = new(body);

            using CancellationTokenSource firstFrame =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            firstFrame.CancelAfter(FirstFrameTimeout);

            if (!await relay.TryBufferFirstPartAsync(firstFrame.Token).ConfigureAwait(false))
            {
                upstream.Dispose();
                return null;
            }

            string contentType = upstream.Content.Headers.ContentType?.ToString()
                                 ?? "multipart/x-mixed-replace";

            return new LiveMjpegStream(upstream, relay, contentType);
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                                    or IOException
                                                    or HttpRequestException)
        {
            upstream.Dispose();
            return null;
        }
    }
}

/// <summary>
/// A live MJPEG stream with its first frame already in hand. Dispose to release the camera.
/// </summary>
public sealed class LiveMjpegStream : IDisposable
{
    private readonly HttpResponseMessage _upstream;
    private readonly MjpegDhtRelay _relay;

    internal LiveMjpegStream(HttpResponseMessage upstream, MjpegDhtRelay relay, string contentType)
    {
        _upstream = upstream;
        _relay = relay;
        ContentType = contentType;
    }

    /// <summary>The upstream's content type, boundary included, for the response to repeat.</summary>
    public string ContentType { get; }

    /// <summary>
    /// Relays the stream — buffered first frame, then everything after it, frames repaired as they
    /// pass. Returns when the upstream ends; cancelling is how a viewer leaving ends the copy, and
    /// what lets the stream server release the camera.
    /// </summary>
    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        return _relay.CopyToAsync(destination, cancellationToken);
    }

    public void Dispose()
    {
        _upstream.Dispose();
    }
}
