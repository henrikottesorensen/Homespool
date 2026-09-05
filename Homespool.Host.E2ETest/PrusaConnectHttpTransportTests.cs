using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.Exceptions;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Model;

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
public sealed class PrusaConnectHttpTransportTests : IAsyncLifetime
{
    private const string TelemetryBody = """{"state":"IDLE","temp_nozzle":27.1,"temp_bed":27.1}""";

    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("e2e-http");
    private readonly CapturingSink _logs = new();
    private CapturingMessageDispatcher? _dispatcher;
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _factory?.Dispose();

        _scratch.Dispose();
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
    /// A body past the ceiling is refused before it is parsed into memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The websocket transport has always had this bound; the HTTP one did not, and the two carry the
    /// same messages — so a body that would cost the socket its connection could be parsed whole here.
    /// It takes a valid fingerprint and token to reach it, so this is a blast-radius limit rather
    /// than a drive-by: a leaked printer credential is a thing that happens, and one should not be
    /// able to stop the server for everyone.
    /// </para>
    /// <para>
    /// Answered 400 rather than 413, because firmware reads status codes here and treats every 4xx
    /// alike — a status it has never been sent would buy nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABodyOverTheCeilingIsRefused()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Well past the 1 MiB default, and valid JSON throughout - so the only thing that can refuse
        // it is the ceiling, not the parser.
        string oversized = $$"""{"state":"IDLE","junk":"{{new string('x', 2 * 1024 * 1024)}}"}""";

        // Act
        using HttpRequestMessage request = Post("/p/telemetry", identity, token, oversized);

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _dispatcher!.Calls.Should().BeEmpty("nothing over the ceiling should reach the dispatcher");
    }

    /// <summary>
    /// The ordinary body must be nowhere near the ceiling, or the bound would be breaking real
    /// printers rather than protecting them.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryBodyIsWellInsideTheCeiling()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody);

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
        _dispatcher!.Calls.Should().NotBeEmpty("a normal telemetry post must still reach the dispatcher");
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

        // One line carrying both, because the enrolment above already produced a "Fingerprint,
        // Token" line for its anonymous /p/register - the same diagnostic doing its job on a
        // different request. Only the conjunction identifies this one.
        _logs.HasEventWith(("MissingHeaders", Headers.Token),
                           ("Fingerprint", PrinterFingerprint.Key(identity.HeaderFingerprint)))
             .Should().BeTrue("the diagnostic must name the absent header and the printer that asked, on one line");
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
        _logs.HasEventWith(("Fingerprint", "0123456789abcdef")).Should().BeTrue();
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

    /// <summary>
    /// The round trip the transport exists for: a command sent through <see cref="PrinterCommandService"/>
    /// is parked, collected by the fake in the response to its next telemetry POST, answered with an
    /// event POST, and the caller sees the answer - through the same actor and the same ack
    /// correlation the socket uses.
    /// </summary>
    /// <remarks>
    /// The fake is set printing first so <c>PAUSE_PRINT</c> is a command
    /// <see cref="FirmwareFaithfulPolicy"/> answers <c>FINISHED</c> rather than rejects; the point is
    /// the plumbing, not the policy. Telemetry is paced at 20 ms so the parked command is collected
    /// well inside the test's patience while still going through a real "next poll".
    /// </remarks>
    [Fact]
    public async Task ACommandIsCollectedInTheNextTelemetryResponseAndItsAnswerReachesTheCaller()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        await using FakePrinterClient fake = new(identity,
                                                 TimeProvider.System,
                                                 new FakePrinterOptions
                                                 {
                                                     TelemetrySource = new PacedTelemetrySource(TimeSpan.FromMilliseconds(20)),
                                                 });

        fake.Token = token;
        fake.Device.StartPrint(jobId: 1);

        using HttpClient printer = PrinterListener.CreateClient(_factory);
        using CancellationTokenSource run = new(TimeSpan.FromSeconds(30));
        Task running = fake.RunHttpAsync(printer, run.Token);

        // The first POST is what creates the session and registers the actor; a command sent before
        // it would be PrinterNotConnected, correctly.
        await WaitUntilAsync(() => _factory.Services.GetRequiredService<PrinterConnectionRegistry>().IsConnected(printerId));

        // Act
        CommandOutcome outcome;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            PrinterCommandService service = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

            outcome = await service.SendCommandAsync(printerId, new PrusaConnect.Commands.PausePrint(), Caller.Unscoped(userId), run.Token)
                      ?? throw new InvalidOperationException("PAUSE_PRINT reported no answer expected.");
        }

        // Assert
        outcome.EventType.Should().Be(PrinterEventType.Finished,
                                      "the fake collected the command in a telemetry response and acked it over /p/events");

        fake.ReceivedCommands.Should().ContainSingle()
            .Which.Kind.Should().Be(ServerCommandKind.Json, "the response's Content-Type is how firmware picks a parser");

        fake.ReplyFault.Should().BeNull();
        _logs.Failures.Should().BeEmpty();

        await run.CancelAsync();
        await running;
    }

    /// <summary>
    /// A printer on this transport reads as connected while it is posting, and as gone once it
    /// stops - with no socket to be open or closed, that is a judgement about recency, and it is the
    /// registry's <c>IsConnected</c> that every page and the queue loop consult.
    /// </summary>
    [Fact]
    public async Task APostingPrinterIsConnectedAndAQuietOneIsNot()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int printerId, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        PrinterConnectionRegistry registry = _factory.Services.GetRequiredService<PrinterConnectionRegistry>();
        registry.IsConnected(printerId).Should().BeFalse("nothing has been heard from it yet");

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using (HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody))
        {
            using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // Assert
        registry.IsConnected(printerId).Should().BeTrue("one authenticated POST is arrival");

        // Not asserted here: that it goes quiet after IdleWindow. That takes 15 s of wall clock and
        // is what ReapingFailsAParkedCommandAsNotConnected drives directly through the sessions.
    }

    /// <summary>
    /// A command parked for a printer that never returns is failed as <c>NotConnected</c> when the
    /// session is reaped - the same outcome a socket death produces, so a caller learns nothing new
    /// and nothing waits forever.
    /// </summary>
    /// <remarks>
    /// Drives <see cref="HttpPrinterSessions.StopAsync"/>, which reaps everything, rather than
    /// waiting out the idle window: the reaper's decision is a timestamp comparison the unit tests
    /// can pin, and what this proves is the teardown - unregister, complete, drain - failing the
    /// parked command through the actor's own loop.
    /// </remarks>
    [Fact]
    public async Task ReapingFailsAParkedCommandAsNotConnected()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        using (HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody))
        {
            using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);
        }

        Task<CommandOutcome?> send;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            PrinterCommandService service = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

            // Parked: nobody polls again, so it sits until the session ends.
            send = service.SendCommandAsync(printerId, new PrusaConnect.Commands.PausePrint(), Caller.Unscoped(userId),
                                            TestContext.Current.CancellationToken);

            // Act
            HttpPrinterSessions sessions = _factory.Services.GetRequiredService<HttpPrinterSessions>();
            await sessions.StopAsync(TestContext.Current.CancellationToken);

            // Assert
            Func<Task> awaitingIt = async () => await send;
            await awaitingIt.Should().ThrowAsync<PrinterNotConnectedException>(
                "a reaped session fails its parked command exactly as a dead socket fails an in-flight one");
        }

        _factory.Services.GetRequiredService<PrinterConnectionRegistry>().IsConnected(printerId)
                .Should().BeFalse("reaping unregisters");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>
    /// The SDK's raw fetch serves an offer only to the printer it was made for. Two printers in two
    /// teams, one offer: the other printer's valid credential gets the same 404 an unknown token
    /// gets, and the intended printer gets the bytes. Without the binding, any enrolled printer that
    /// had seen an offer token - which the encrypted path puts in a plain-HTTP URL as the IV - could
    /// read the file here in the clear.
    /// </summary>
    [Fact]
    public async Task ARawFetchServesAnOfferOnlyToThePrinterItWasMadeFor()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity intended, string intendedToken, int intendedId, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);
        (PrinterIdentity other, string otherToken, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        string directory = Path.Combine(Path.GetTempPath(), $"hs-e2e-raw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(4096);

            string path = Path.Combine(directory, "model.gcode");
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

            string hash = Guid.NewGuid().ToString("N")[..27];
            _factory.Services.GetRequiredService<ITransferOffers>().Offer(hash, path, intendedId).Should().BeTrue();

            using HttpClient printer = PrinterListener.CreateClient(_factory);

            // Act
            using HttpRequestMessage strangerRequest = Get($"/p/teams/1/files/{hash}/raw", other, otherToken);
            using HttpResponseMessage strangerResponse = await printer.SendAsync(strangerRequest, TestContext.Current.CancellationToken);

            using HttpRequestMessage ownRequest = Get($"/p/teams/1/files/{hash}/raw", intended, intendedToken);
            using HttpResponseMessage ownResponse = await printer.SendAsync(ownRequest, TestContext.Current.CancellationToken);

            // Assert
            strangerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
                                                    "a valid credential for another printer must look exactly like an unknown token");

            ownResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the refusal must not have consumed the offer");
            (await ownResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Should().Equal(bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpRequestMessage Get(string route, PrinterIdentity identity, string token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, route);

        request.Headers.Add(Headers.Fingerprint, identity.HeaderFingerprint);
        request.Headers.Add(Headers.Token, token);
        request.Headers.Add(Headers.UserAgentPrinter, "Buddy");
        request.Headers.Add(Headers.UserAgentVersion, "6.2.6");

        return request;
    }

    private static HttpRequestMessage Post(string route, PrinterIdentity identity, string token, string body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, route)
        {
            Content = JsonBody(body),
        };

        // The 16-character header form, which is what a real printer sends on every request of this
        // transport - see PrinterFingerprint.
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

        content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);

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

    /// <summary>
    /// A telemetry source that never ends and paces itself, for a run that is cancelled rather than
    /// awaited - the shape a real printer has, at a cadence a test can afford.
    /// </summary>
    private sealed class PacedTelemetrySource : ITelemetrySource
    {
        private readonly TimeSpan _interval;
        private readonly TelemetryReadings _readings = new();

        public PacedTelemetrySource(TimeSpan interval)
        {
            _interval = interval;
        }

        public byte[]? NextMessage(FakeDevice device)
        {
            return TelemetryMessageBuilder.BuildSlim(device, _readings);
        }

        public TimeSpan DelayBeforeNext(FakeDevice device)
        {
            return _interval;
        }
    }

    private void Start(CapturingMessageDispatcher? dispatcher)
    {
        _factory = new HomespoolFactory(_scratch, dispatcher, _logs);

        // Force startup - migrations and AdminBootstrap - before a test touches the server, rather
        // than lazily on the first request.
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
    }
}
