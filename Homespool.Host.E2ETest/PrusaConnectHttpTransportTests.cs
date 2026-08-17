using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.FakePrinter;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Drives the pre-websocket HTTP transport - <c>POST /p/telemetry</c> and <c>POST /p/events</c> -
/// through the real pipeline: listener segregation, the Fingerprint/Token auth handler and
/// <c>[Authorize]</c>, then the dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// The peer these serve is firmware built with <c>WEBSOCKET</c> off - a 6.2.6 MK3.5 speaks it, and so
/// does <c>connect_rig</c> configured the same way. It is <b>Buddy's</b> HTTP dialect rather than the
/// Python SDK's, which differs in fingerprint length and telemetry fields; nothing here is evidence
/// about an SDK-driven printer.
/// </para>
/// <para>
/// Split by what each test can prove: the capturing dispatcher answers "did auth resolve the right
/// printer and thread it through", and returns null so nothing persists, while
/// <see cref="TelemetryIsAcceptedThroughTheRealDispatcherWithoutError"/> runs the production
/// dispatcher so the mapping into <see cref="Telemetry.ITelemetrySink"/> - the only code these
/// endpoints add beyond the actor's own - actually executes.
/// </para>
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class PrusaConnectHttpTransportTests : IAsyncLifetime, IDisposable
{
    private const string TelemetryBody = """{"state":"IDLE","temp_nozzle":27.1,"temp_bed":27.1}""";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-e2e-http-{Guid.NewGuid():N}.db");
    private readonly CapturingSink _logs = new();
    private CapturingMessageDispatcher? _dispatcher;
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _factory?.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A claimed printer's telemetry is accepted, and the printer id the auth handler resolved from
    /// the Fingerprint header - not one the body could claim - is what reaches the dispatcher.
    /// </summary>
    [Fact]
    public async Task TelemetryReachesTheDispatcherWithTheClaimedPrinterId()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int printerId, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody);

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
                                        "204 is the transport's own 'no command pending'");

        _dispatcher!.Calls.Should().ContainSingle().Which.printerId.Should().Be(printerId);
    }

    /// <summary>
    /// An event posted to the other route reaches the same dispatcher. Both routes share one handler,
    /// because the payload rather than the URL decides what a message is.
    /// </summary>
    [Fact]
    public async Task EventsReachTheDispatcherToo()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int printerId, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post("/p/events", identity, token, """{"event":"INFO","command_id":1}""");

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _dispatcher!.Calls.Should().ContainSingle().Which.printerId.Should().Be(printerId);
    }

    /// <summary>
    /// The production dispatcher's output is mapped and handed to the telemetry sink without anything
    /// failing behind the response - which a 204 alone would not show, since an exception thrown after
    /// the response starts reaches no client.
    /// </summary>
    [Fact]
    public async Task TelemetryIsAcceptedThroughTheRealDispatcherWithoutError()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody);

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _logs.Failures.Should().BeEmpty("mapping telemetry into the sink must not throw behind the response");
    }

    /// <summary>
    /// Credentials are the gate, exactly as they are on <c>/p/ws</c> - the endpoints carry no
    /// anonymous path of their own.
    /// </summary>
    [Fact]
    public async Task UnknownCredentialsAreRejected()
    {
        // Arrange
        StartWithCapturingDispatcher();

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        using HttpRequestMessage request = new(HttpMethod.Post, "/p/telemetry")
        {
            Content = JsonBody(TelemetryBody),
        };

        request.Headers.Add(Headers.Fingerprint, "no-such-fingerprint");
        request.Headers.Add(Headers.Token, "irrelevant-token");

        // Act
        using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _dispatcher!.Calls.Should().BeEmpty("an unauthenticated body must never reach the dispatcher");
    }

    /// <summary>
    /// A body that is not JSON is the client's protocol violation, answered with 400 rather than an
    /// error of ours. On the socket the same garbage costs the connection; here the printer simply
    /// retries.
    /// </summary>
    [Fact]
    public async Task ABodyThatIsNotJsonIsRefused()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post("/p/telemetry", identity, token, "not json at all");

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _logs.Failures.Should().BeEmpty("a malformed body is the client's fault, not an error of ours");
    }

    /// <summary>
    /// The transport lives on the printer listener alone, like the rest of <c>/p/*</c> - reaching it
    /// on the user port is a 404 before authentication is even attempted.
    /// </summary>
    [Fact]
    public async Task TheEndpointsAreAbsentFromTheUserListener()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient user = _factory.CreateClient();

        // Act
        using HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody);

        using HttpResponseMessage response =
            await user.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A printer that omits the Token header - the Python SDK's shape before registration completes,
    /// since <c>make_headers</c> adds it only once there is one - is refused, and the log says which
    /// header was missing and which fingerprint asked. Without both, this case is indistinguishable
    /// from a caller sending no headers at all.
    /// </summary>
    [Fact]
    public async Task AMissingTokenHeaderIsRefusedAndNamesTheFingerprintAndTheHeader()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string _, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        using HttpRequestMessage request = new(HttpMethod.Post, "/p/telemetry")
        {
            Content = JsonBody(TelemetryBody),
        };

        request.Headers.Add(Headers.Fingerprint, identity.HeaderFingerprint);
        request.Headers.Add(Headers.UserAgentPrinter, "Buddy");
        request.Headers.Add(Headers.UserAgentVersion, "6.2.6");

        // Act
        using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        _logs.FindPropertyValue("MissingHeaders").Should().Be(Headers.Token,
                                                             "the diagnostic must name the header that was absent");

        _logs.FindPropertyValue("Fingerprint")
             .Should().Be(PrinterFingerprint.Key(identity.HeaderFingerprint),
                          "and the printer that asked, in the key form successes are logged under");
    }

    /// <summary>
    /// A fingerprint matching no printer names itself in the log, which is what separates one printer
    /// retrying with a credential we no longer hold from a caller trying fingerprints it invented.
    /// </summary>
    [Fact]
    public async Task AnUnknownFingerprintIsNamedInTheDiagnostic()
    {
        // Arrange
        StartWithCapturingDispatcher();

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        using HttpRequestMessage request = new(HttpMethod.Post, "/p/telemetry")
        {
            Content = JsonBody(TelemetryBody),
        };

        request.Headers.Add(Headers.Fingerprint, "0123456789abcdef");
        request.Headers.Add(Headers.Token, "irrelevant-token");
        request.Headers.Add(Headers.UserAgentPrinter, "Buddy");
        request.Headers.Add(Headers.UserAgentVersion, "6.2.6");

        // Act
        using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _logs.FindPropertyValue("Fingerprint").Should().Be("0123456789abcdef");
    }

    /// <summary>
    /// <see cref="FakePrinterClient.RunHttpAsync"/> - what <c>fakeprinter run --no-websocket</c> uses -
    /// drives the transport with no socket anywhere: the connect-time INFO followed by each telemetry
    /// message, every one of them authenticated and attributed to the enrolled printer.
    /// </summary>
    [Fact]
    public async Task TheFakePrinterDrivesTheTransportWithoutASocket()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int printerId, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        FiniteTelemetrySource source = new(3);

        await using FakePrinterClient fake = new(identity,
                                                 TimeProvider.System,
                                                 new FakePrinterOptions { TelemetrySource = source });

        fake.Token = token;

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        await fake.RunHttpAsync(printer, TestContext.Current.CancellationToken);

        // Assert
        _dispatcher!.Calls.Should().HaveCount(4, "the INFO event, then one call per telemetry message");
        _dispatcher.Calls.Should().AllSatisfy(call => call.printerId.Should().Be(printerId));

        _dispatcher.Calls[0].root.TryGetProperty("event", out _)
                   .Should().BeTrue("INFO goes first on this transport exactly as it does on the socket");
    }

    private static HttpRequestMessage Post(string route, PrinterIdentity identity, string token, string body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, route)
        {
            Content = JsonBody(body),
        };

        // The 16-character header form, which is what a real printer sends on every request of this
        // transport - see PrinterFingerprint and notes/cross-channel-identity-bug.md.
        request.Headers.Add(Headers.Fingerprint, identity.HeaderFingerprint);
        request.Headers.Add(Headers.Token, token);

        // All four are required: the handler answers NoResult - and therefore 401 - if any is absent,
        // so credentials alone do not authenticate. 6.2.6 is the version that reaches these endpoints
        // on real hardware, that being the last release built with WEBSOCKET off.
        request.Headers.Add(Headers.UserAgentPrinter, "Buddy");
        request.Headers.Add(Headers.UserAgentVersion, "6.2.6");

        return request;
    }

    private static StringContent JsonBody(string body)
    {
        StringContent content = new(body, Encoding.UTF8);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return content;
    }

    private void StartWithCapturingDispatcher()
    {
        _dispatcher = new CapturingMessageDispatcher();

        Start(_dispatcher);
    }

    private void StartWithRealDispatcher()
    {
        Start(null);
    }

    /// <summary>
    /// A telemetry source that ends, so a run can be awaited rather than cancelled. The stock
    /// <see cref="SyntheticTelemetrySource"/> never returns null, which is right for a rig and wrong
    /// for an exact-count assertion.
    /// </summary>
    private sealed class FiniteTelemetrySource : ITelemetrySource
    {
        private readonly int _count;
        private readonly TelemetryReadings _readings = new();
        private int _sent;

        public FiniteTelemetrySource(int count)
        {
            _count = count;
        }

        public byte[]? NextMessage(FakeDevice device)
        {
            return _sent++ < _count ? TelemetryMessageBuilder.BuildSlim(device, _readings) : null;
        }

        public TimeSpan DelayBeforeNext(FakeDevice device)
        {
            return TimeSpan.Zero;
        }
    }

    private void Start(CapturingMessageDispatcher? dispatcher)
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}", dispatcher, _logs);

        // Force startup - migrations and AdminBootstrap - before a test touches the server, rather
        // than lazily on the first request.
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }
}
