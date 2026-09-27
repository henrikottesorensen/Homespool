using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Testing;

using NSubstitute;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="Go2RtcClient.PutStreamAsync"/> - which of the sidecar's refusals was which, since the
/// cameras page sends somebody to a different place for each.
/// </summary>
/// <remarks>
/// The bodies are go2rtc 1.9.14's own: the first three are errors of its streams package, the next
/// three what its PUT handler answers when patching or writing go2rtc.yaml fails - all with 400, and
/// all with the newline http.Error appends.
/// </remarks>
public sealed class StreamRegistrationTests
{
    private const string Source = "rtsp://admin:hunter2@camera.example/live"; // betterleaks:allow - a made-up camera

    [Theory]
    [InlineData(HttpStatusCode.OK, "", StreamRegistration.Registered)]
    [InlineData(HttpStatusCode.BadRequest, "streams: source not supported\n", StreamRegistration.SourceRefused)]
    [InlineData(HttpStatusCode.BadRequest, "streams: source from insecure producer\n", StreamRegistration.SourceRefused)]
    [InlineData(HttpStatusCode.BadRequest, "streams: source with spaces may be insecure\n", StreamRegistration.SourceRefused)]
    [InlineData(HttpStatusCode.BadRequest, "yaml: line 1: did not find expected key\n", StreamRegistration.ConfigurationNotSaved)]
    [InlineData(HttpStatusCode.BadRequest, "open /config/go2rtc.yaml: permission denied\n", StreamRegistration.ConfigurationNotSaved)]
    [InlineData(HttpStatusCode.BadRequest, "config file disabled\n", StreamRegistration.ConfigurationNotSaved)]
    [InlineData(HttpStatusCode.Unauthorized, "Unauthorized\n", StreamRegistration.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "streams: source not supported\n", StreamRegistration.Unavailable)]
    public async Task EachAnswerIsTheRegistrationItDescribes(HttpStatusCode status, string body, StreamRegistration expected)
    {
        using AnsweringHandler handler = new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

        StreamRegistration registration = await Client(handler, new FakeLogger<Go2RtcClient>())
                                                .PutStreamAsync(Guid.NewGuid(), Source, CancellationToken.None);

        registration.Should().Be(expected);
    }

    [Fact]
    public async Task AnUnreachableSidecarIsUnavailable()
    {
        using AnsweringHandler handler = new(_ => throw new HttpRequestException("Connection refused"));

        StreamRegistration registration = await Client(handler, new FakeLogger<Go2RtcClient>())
                                                .PutStreamAsync(Guid.NewGuid(), Source, CancellationToken.None);

        registration.Should().Be(StreamRegistration.Unavailable);
    }

    /// <summary>
    /// The unwritable file is the one an administrator has to act on, so its reason reaches the log -
    /// and the source, which carries the camera's password, is taken out of it first.
    /// </summary>
    [Fact]
    public async Task AnUnsavedConfigurationLogsTheSidecarsReasonWithoutTheSource()
    {
        using AnsweringHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($"yaml: line 3: could not find expected ':' near {Source}\n"),
        });
        FakeLogger<Go2RtcClient> logger = new();

        await Client(handler, logger).PutStreamAsync(Guid.NewGuid(), Source, CancellationToken.None);

        FakeLogRecord record = logger.Collector.GetSnapshot().Should().ContainSingle().Which;
        string reason = record.StructuredState!.Single(pair => pair.Key == "Reason").Value!;

        reason.Should().StartWith("yaml: line 3: could not find expected ':'");
        reason.Should().Contain("<source>").And.NotContain("hunter2");
        record.Message.Should().NotContain("hunter2");
    }

    private static Go2RtcClient Client(HttpMessageHandler handler, FakeLogger<Go2RtcClient> logger)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new Go2RtcClient(factory,
                                TestOptions.Monitor(new CameraOptions
                                {
                                    ApiUsername = "homespool",
                                    ApiPassword = "secret", // betterleaks:allow - the sidecar is a handler in this file
                                }),
                                logger);
    }

    /// <summary>Answers every request as told.</summary>
    private sealed class AnsweringHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(answer(request));
        }
    }
}
