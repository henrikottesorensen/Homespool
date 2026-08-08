using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// The guards on reading a camera. Each one exists because the thing on the other end is not ours
/// and may be anything at all.
/// </summary>
public class CameraSnapshotFetcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The sidecar address the application actually fetches - never a camera's own.</summary>
    private static readonly Uri Frame = new("http://go2rtc:1984/api/frame.jpeg?src=abc");

    [Fact]
    public async Task AnImageIsReturnedWithTheTimeItWasFetched()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02];
        using RecordingHandler handler = Respond(HttpStatusCode.OK, "image/jpeg", jpeg);
        CameraSnapshotFetcher fetcher = Build(handler);

        CameraFrame? frame = await fetcher.FetchAsync(Frame, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Bytes.Should().Equal(jpeg);
        frame.ContentType.Should().Be("image/jpeg");
        frame.CapturedAt.Should().Be(Now);
    }

    /// <summary>
    /// A camera answering HTML is usually a login page or an error. Storing those bytes as "the
    /// frame" would render a web page where somebody expected their printer.
    /// </summary>
    [Fact]
    public async Task AResponseThatIsNotAnImageIsRefused()
    {
        using RecordingHandler handler =
            Respond(HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes("<html>login</html>"));
        CameraSnapshotFetcher fetcher = Build(handler);

        (await fetcher.FetchAsync(Frame, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AFailureStatusIsRefused()
    {
        using RecordingHandler handler = Respond(HttpStatusCode.NotFound, "image/jpeg", [1, 2, 3]);
        CameraSnapshotFetcher fetcher = Build(handler);

        (await fetcher.FetchAsync(Frame, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ADeclaredLengthOverTheLimitIsRefusedBeforeReading()
    {
        using RecordingHandler handler = Respond(HttpStatusCode.OK, "image/jpeg", new byte[64]);
        CameraSnapshotFetcher fetcher = Build(handler, maxFrameBytes: 16);

        (await fetcher.FetchAsync(Frame, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// The one that matters: a chunked response declares no length at all, so the cap has to hold
    /// while reading. Without it an endless response costs the process rather than the limit.
    /// </summary>
    [Fact]
    public async Task AnUndeclaredBodyOverTheLimitIsRefusedWhileReading()
    {
        using RecordingHandler handler = RespondChunked("image/jpeg", new byte[64]);
        CameraSnapshotFetcher fetcher = Build(handler, maxFrameBytes: 16);

        (await fetcher.FetchAsync(Frame, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnUnreachableCameraIsNullRatherThanAnException()
    {
        using ThrowingHandler handler = new();
        CameraSnapshotFetcher fetcher = Build(handler);

        (await fetcher.FetchAsync(Frame, CancellationToken.None)).Should().BeNull();
    }

    private static CameraSnapshotFetcher Build(HttpMessageHandler handler, long maxFrameBytes = 4L * 1024 * 1024)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        CameraOptions options = new() { MaxFrameBytes = maxFrameBytes };

        return new CameraSnapshotFetcher(
            factory,
            Options.Create(options),
            new FakeTimeProvider(Now),
            NullLogger<CameraSnapshotFetcher>.Instance);
    }

    private static RecordingHandler Respond(HttpStatusCode status, string contentType, byte[] body)
    {
        return new RecordingHandler(() =>
        {
            HttpResponseMessage response = new(status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return response;
        });
    }

    /// <summary>
    /// A body with no <c>Content-Length</c>, which is what a chunked camera stream looks like.
    /// </summary>
    private static RecordingHandler RespondChunked(string contentType, byte[] body)
    {
        return new RecordingHandler(() =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new System.IO.MemoryStream(body)),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            response.Content.Headers.ContentLength = null;
            return response;
        });
    }

    /// <summary>
    /// A working handler rather than a substitute: it has to produce a real response body, which is
    /// what the size cap is read from.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;

        public RecordingHandler(Func<HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_factory());
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("no route to host");
        }
    }
}
