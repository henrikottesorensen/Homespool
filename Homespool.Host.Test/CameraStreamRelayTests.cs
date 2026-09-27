using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

using NSubstitute;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CameraStreamRelay"/>'s answer to what the sidecar calls its stream. The response's type
/// decides whether a browser opening the stream URL treats it as a document, so it is composed rather
/// than repeated, and an upstream that is not a multipart stream is not relayed at all.
/// </summary>
/// <remarks>
/// Every body here is a whole, well-formed first part, so a refusal is the type's doing and never the
/// first-frame wait's.
/// </remarks>
public sealed class CameraStreamRelayTests
{
    [Fact]
    public async Task TheSidecarsOwnTypeIsAnsweredWithItsBoundary()
    {
        using LiveMjpegStream? live = await OpenAsync("multipart/x-mixed-replace; boundary=frame");

        live.Should().NotBeNull();
        live!.ContentType.Should().Be("multipart/x-mixed-replace; boundary=frame");
    }

    /// <summary>
    /// Quotes are the header's syntax, not part of the boundary, and the one written back needs none.
    /// </summary>
    [Fact]
    public async Task AQuotedBoundaryIsWrittenBackUnquoted()
    {
        using LiveMjpegStream? live = await OpenAsync("multipart/x-mixed-replace; boundary=\"frame\"");

        live.Should().NotBeNull();
        live!.ContentType.Should().Be("multipart/x-mixed-replace; boundary=frame");
    }

    /// <summary>
    /// Anything but a multipart stream is refused, however well the body is framed: served in this
    /// origin, text/html is a page and image/svg+xml runs script. Each carries a boundary that would
    /// pass, so it is the type alone that refuses them.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("text/html; boundary=frame")]
    [InlineData("image/svg+xml; boundary=frame")]
    [InlineData("multipart/mixed; boundary=frame")]
    public async Task AnotherTypeIsRefused(string contentType)
    {
        using LiveMjpegStream? live = await OpenAsync(contentType);

        live.Should().BeNull();
    }

    [Fact]
    public async Task NoTypeAtAllIsRefused()
    {
        using LiveMjpegStream? live = await OpenAsync(contentType: null);

        live.Should().BeNull();
    }

    /// <summary>
    /// A multipart type with no boundary names no way to find the parts. The browser could do nothing
    /// with it either.
    /// </summary>
    [Fact]
    public async Task AMultipartTypeWithNoBoundaryIsRefused()
    {
        using LiveMjpegStream? live = await OpenAsync("multipart/x-mixed-replace");

        live.Should().BeNull();
    }

    /// <summary>
    /// The boundary is the one thing taken from upstream, so it is held to what can be written back
    /// with no quoting: past RFC 2046's 70 characters, or holding anything else, it is refused.
    /// </summary>
    [Theory]
    [InlineData("multipart/x-mixed-replace; boundary=\"fr@me\"")]
    [InlineData("multipart/x-mixed-replace; boundary=\"fr ame\"")]
    [InlineData("multipart/x-mixed-replace; boundary=\"\"")]
    public async Task ABoundaryThatCannotBeWrittenBackAsItStandsIsRefused(string contentType)
    {
        using LiveMjpegStream? live = await OpenAsync(contentType);

        live.Should().BeNull();
    }

    [Fact]
    public async Task ABoundaryLongerThanSeventyCharactersIsRefused()
    {
        using LiveMjpegStream? live = await OpenAsync($"multipart/x-mixed-replace; boundary={new string('a', 71)}");

        live.Should().BeNull();
    }

    [Fact]
    public async Task ABoundaryOfSeventyCharactersIsAccepted()
    {
        string boundary = new('a', 70);

        using LiveMjpegStream? live = await OpenAsync($"multipart/x-mixed-replace; boundary={boundary}", boundary);

        live.Should().NotBeNull();
    }

    /// <summary>
    /// A refusal says what the sidecar sent: the viewer is only told there were no pictures.
    /// </summary>
    [Fact]
    public async Task ARefusalIsLoggedWithTheTypeTheSidecarSent()
    {
        FakeLogger<CameraStreamRelay> logger = new();

        using LiveMjpegStream? live = await OpenAsync("text/html", logger: logger);

        FakeLogRecord record = logger.Collector.GetSnapshot().Should().ContainSingle().Which;
        record.Level.Should().Be(LogLevel.Warning);
        record.StructuredState!.Single(pair => pair.Key == "ContentType").Value.Should().Be("text/html");
    }

    private static async Task<LiveMjpegStream?> OpenAsync(string? contentType,
                                                          string boundary = "frame",
                                                          ILogger<CameraStreamRelay>? logger = null)
    {
        using AnsweringHandler handler = new(() =>
        {
            byte[] part = [.. Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: 3\r\n\r\n"),
                           0xFF, 0xD8, 0xFF,
                           .. Encoding.ASCII.GetBytes("\r\n")];

            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(part) };

            if (contentType is not null)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType).Should().BeTrue();
            }

            return response;
        });

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        Go2RtcClient streamServer = new(factory,
                                        TestOptions.Monitor(new CameraOptions
                                        {
                                            ApiUsername = "homespool",
                                            ApiPassword = "secret", // betterleaks:allow - the sidecar is a handler in this file
                                        }),
                                        NullLogger<Go2RtcClient>.Instance);

        CameraStreamRelay relay = new(streamServer, logger ?? NullLogger<CameraStreamRelay>.Instance);

        return await relay.OpenAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
    }

    /// <summary>Answers every request the same way.</summary>
    private sealed class AnsweringHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(answer());
        }
    }
}
