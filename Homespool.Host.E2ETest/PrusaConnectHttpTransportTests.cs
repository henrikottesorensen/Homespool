using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Accounts;
using Homespool.Host.Controllers;
using Homespool.Host.Exceptions;
using Homespool.Host.PrintFiles;
using Homespool.Host.Printing;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Telemetry;
using Homespool.Model;
using Homespool.Model.Entities;

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
/// about an SDK-driven printer, beyond the messages
/// <see cref="AnSdkTransferWithAnIdAboveIntMaxValueRecordsTheFileArriving"/> renders by hand from
/// the SDK's source.
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
        _logs.WithException.Should().BeEmpty("the refusal is said in a sentence; the parser's stack trace says nothing more");
    }

    /// <summary>
    /// JSON that parses but is not a printer message is refused exactly like JSON that does not
    /// parse. The dispatcher is what finds these out, so it runs for real here.
    /// </summary>
    /// <param name="body">A document the parser accepts and the dispatcher cannot classify: a root
    /// that is not an object, a <c>transfer</c> that is not a string, and telemetry without its
    /// required <c>state</c>.</param>
    [Theory]
    [InlineData("[1]")]
    [InlineData("\"IDLE\"")]
    [InlineData("""{"transfer":5}""")]
    [InlineData("""{"temp_nozzle":27.1}""")]
    public async Task ABodyThatIsJsonButNotAMessageIsRefused(string body)
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post("/p/telemetry", identity, token, body);

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _logs.Failures.Should().BeEmpty("a malformed body is the client's fault, not an error of ours");
        _logs.WithException.Should().BeEmpty("the refusal is said in a sentence; the parser's stack trace says nothing more");
    }

    /// <summary>
    /// A body carrying a non-finite number is accepted, with the production dispatcher behind it.
    /// </summary>
    /// <remarks>
    /// This is what the Python SDK posts for a valid gcode file whose metadata says
    /// <c>layer_height = nan</c>: <c>float()</c> reads it and <c>json.dumps</c> writes it back as a
    /// bare <c>NaN</c>, spaces after the colons and all. It used to be a 400 on every retry, for a
    /// number nothing here reads.
    /// </remarks>
    [Fact]
    public async Task ABodyCarryingANonFiniteNumberIsAccepted()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post(
            "/p/events",
            identity,
            token,
            """{"event": "FILE_INFO", "state": "IDLE", "data": {"size": 1024, "layer_height": NaN, "max_layer_z": Infinity, "path": "/usb/NAN.GCO"}}""");

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _logs.Failures.Should().BeEmpty("mending the body must not leave anything failing behind the response");
    }

    /// <summary>
    /// An event word this build does not know is accepted and dropped, with the production dispatcher
    /// behind it, and said once however often it is posted. It is a word firmware added rather than a
    /// malformed body, and a 400 would only have the printer post it again.
    /// </summary>
    [Fact]
    public async Task AnUnknownEventWordIsAcceptedAndSaidOnce()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage request = Post("/p/events", identity, token, """{"event":"SPOOL_JOINED","state":"IDLE"}""");

            using HttpResponseMessage response =
                await printer.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // Assert
        _logs.CountEventsWith(("Complaint", "sent an event this build does not know, which was dropped"),
                              ("Detail", "\"SPOOL_JOINED\""))
             .Should().Be(1, "two posts inside one window are one line");
        _logs.Failures.Should().BeEmpty("a dropped event must not leave anything failing behind the response");
        _logs.WithException.Should().BeEmpty();
    }

    /// <summary>
    /// Mending non-finite numbers has not made the transport forgiving of anything else.
    /// </summary>
    [Theory]
    [InlineData("""{"event": "FILE_INFO", "data": {"layer_height": NaNx}}""")]
    [InlineData("""{"event": "FILE_INFO", "data": {"layer_height": NaN,, }}""")]
    [InlineData("NaN")]
    public async Task AlmostANonFiniteNumberIsStillRefused(string body)
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // Act
        using HttpRequestMessage request = Post("/p/events", identity, token, body);

        using HttpResponseMessage response =
            await printer.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _logs.Failures.Should().BeEmpty("a malformed body is the client's fault, not an error of ours");
        _logs.WithException.Should().BeEmpty("the refusal is said in a sentence; the parser's stack trace says nothing more");
    }

    /// <summary>
    /// A printer that keeps posting what is refused is told so in the log once, not once per request.
    /// </summary>
    /// <remarks>
    /// The rate limiter bounds these at three a second per token, which is still three lines a second
    /// for as long as the sender likes. Two refusals are covered here because they were the two left
    /// speaking every time: a body over the ceiling, and an inline transfer chunk asked for over a
    /// transport that has no socket to answer on.
    /// </remarks>
    /// <param name="complaint">What the log is expected to say, once.</param>
    /// <param name="oversized">Whether the body is past the ceiling, or an inline chunk request.</param>
    [Theory]
    [InlineData("posted a body over the size ceiling", true)]
    [InlineData("requested an inline transfer chunk over HTTP, which cannot be served", false)]
    public async Task ARepeatedRefusalIsLoggedOnce(string complaint, bool oversized)
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        string body = """{"transfer":"inline","file_id":1,"start":0,"end":1023}""";

        if (oversized)
        {
            body = $$"""{"state":"IDLE","junk":"{{new string('x', 2 * 1024 * 1024)}}"}""";
        }

        // Act
        for (int i = 0; i < 4; i++)
        {
            using HttpRequestMessage request = Post("/p/telemetry", identity, token, body);

            using HttpResponseMessage response =
                await printer.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // Assert
        _logs.CountEventsWith(("Complaint", complaint)).Should().Be(1, "four refusals inside one window are one line");
        _logs.Failures.Should().BeEmpty();
        _logs.WithException.Should().BeEmpty();
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

            outcome = await service.SendCommandAsync(printerId, new PrusaConnect.Commands.PausePrint(), Caller.Unscoped(userId), run.Token) ??
                      throw new InvalidOperationException("PAUSE_PRINT reported no answer expected.");
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

    /// <summary>
    /// A printer displaced by another connection under its credential takes its identity back with its
    /// next post, as last-wins intends - rather than every post failing against the displaced session.
    /// </summary>
    /// <remarks>
    /// The other connection is a stand-in registered straight into the registry, because what matters
    /// is the registry's displacement and not the socket that caused it. Before the sessions checked
    /// for it, the second post answered 500: the session handed back the actor the registry had
    /// completed, and the post threw on its closed mailbox.
    /// </remarks>
    [Fact]
    public async Task APrinterDisplacedByAnotherConnectionTakesItsIdentityBackOnTheNextPost()
    {
        // Arrange
        StartWithCapturingDispatcher();

        (PrinterIdentity identity, string token, int printerId, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        using (HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody))
        {
            using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        PrinterConnectionRegistry registry = _factory.Services.GetRequiredService<PrinterConnectionRegistry>();
        StandInLink other = new();
        registry.Register(printerId, other, overPlaintext: false);

        // Act
        using (HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody))
        {
            using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the printer's own post is a fresh session");
        }

        registry.TryGet(printerId, out IPrinterLink? live).Should().BeTrue();
        live.Should().NotBeSameAs(other, "the printer's post displaced the other connection in its turn");
        other.Completed.Should().BeTrue();
    }

    /// <summary>
    /// A message that reaches a connection as it is displaced is answered 503, which firmware keeps
    /// and sends again, rather than a 500, which it throws away - and the resend is accepted.
    /// </summary>
    /// <remarks>
    /// The displacement has to land between the post's lookup of its session and its post to the
    /// actor, a window no request can be timed into. So this host's actors register a stand-in under
    /// the printer the first time anything is posted to one, which is that window exactly.
    /// </remarks>
    [Fact]
    public async Task AMessageThatMeetsADisplacementIsRefusedForTheFirmwareToSendAgain()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int _, long _) = await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using WebApplicationFactory<PrinterAppController> displacing =
            _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
                                            services => services.AddSingleton<PrinterConnectionActorFactory, DisplacedOnFirstPostActorFactory>()));

        // A derived factory is a second host with its own in-memory SetupState.
        using (IServiceScope scope = displacing.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();
        }

        using HttpClient printer = PrinterListener.CreateClient(displacing);

        // Act
        HttpStatusCode first;
        HttpStatusCode resent;

        using (HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody))
        {
            using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

            first = response.StatusCode;
        }

        using (HttpRequestMessage request = Post("/p/telemetry", identity, token, TelemetryBody))
        {
            using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

            resent = response.StatusCode;
        }

        // Assert
        first.Should().Be(HttpStatusCode.ServiceUnavailable, "the actor it was posted to had been displaced");
        resent.Should().Be(HttpStatusCode.NoContent, "the resend's lookup finds a fresh session");
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

        // The offered file lives in the test's own scratch directory, whose teardown removes it.
        byte[] bytes = RandomNumberGenerator.GetBytes(4096);
        string path = Path.Combine(_scratch.Path, "model.gcode");

        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        string hash = Guid.NewGuid().ToString("N")[..27];
        _factory.Services.GetRequiredService<ITransferOffers>().Offer(hash, path, intendedId).Should().NotBeNull();

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

    /// <summary>
    /// A file sent to a Python-SDK printer whose download draws an id past <see cref="int.MaxValue"/>
    /// arrives. The SDK's <c>transfer_id</c> is <c>randint(0, 2**32 - 1)</c> (download.py:81-83) and
    /// rides at the root of the answer to the download command, of every telemetry message while it
    /// runs and of its terminal event. Read as a signed int, each was a 400 the SDK logs at debug and
    /// drops (__init__.py:834-837), so for half of all transfers nothing recorded the file arriving.
    /// </summary>
    /// <remarks>
    /// The printer is played by hand, message for message as <c>Command.accept</c>,
    /// <c>DownloadMgr.start</c> and the download loop render them, because the fake printer speaks
    /// Buddy's dialect and Buddy's id is 31-bit. The raw fetch itself is left out: it does not carry
    /// the id, and <see cref="ARawFetchServesAnOfferOnlyToThePrinterItWasMadeFor"/> covers it.
    /// </remarks>
    [Fact]
    public async Task AnSdkTransferWithAnIdAboveIntMaxValueRecordsTheFileArriving()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);
        const uint transferId = uint.MaxValue;

        await PostAsSdkAsync(printer, "/p/telemetry", identity, token, """{"state":"IDLE"}""");

        (HSFile file, StoredFile stored) = await StoreFileAsync(userId, "sdk-cube.gcode");
        Task<DirectSendResult> sending = SendDirectAsync(printerId, file, stored, userId);

        (uint commandId, JsonElement command) = await CollectCommandAsync(printer, identity, token);

        // Act
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"ACCEPTED","source":"CONNECT","data":{},"command_id":{{commandId}},"state":"IDLE"}""");
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"TRANSFER_INFO","source":"CONNECT","data":{"start_cmd_id":{{commandId}},"type":"FROM_CONNECT","path":"/local/sdk-cube.gcode","url":"http://homespool.lan/p/teams/1/files/hash/raw","progress":0.0,"transferred":0,"time_transferring":0,"to_print":false},"command_id":{{commandId}},"transfer_id":{{transferId}},"state":"IDLE"}""");
        await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await PostAsSdkAsync(printer, "/p/telemetry", identity, token,
                             $$"""{"transfer_id":{{transferId}},"transfer_progress":50.0,"transfer_transferred":512,"time_transferring":1,"state":"IDLE"}""");
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"TRANSFER_FINISHED","source":"CONNECT","data":{"start_cmd_id":{{commandId}},"path":"/local/sdk-cube.gcode"},"transfer_id":{{transferId}},"state":"IDLE"}""");

        // Assert
        DateTimeOffset? arrivedAt = await ArrivedAtAsync(printerId, file.Id);

        arrivedAt.Should().NotBeNull("TRANSFER_FINISHED named the attempt the send recorded");
        command.GetProperty("command").GetString().Should().Be("START_CONNECT_DOWNLOAD", "an SDK client fetches the plain download");
        _logs.Failures.Should().BeEmpty();
    }

    /// <summary>
    /// A download a Python-SDK printer accepts and then refuses is a refusal, and the file is not
    /// recorded as taken. The SDK accepts every command before running it (<c>Command.accept</c>,
    /// command.py:87-105); <c>DownloadMgr.start</c> then finds another transfer running and the
    /// command finishes <c>REJECTED</c> (download.py:292-297, command.py:176-177).
    /// </summary>
    /// <remarks>
    /// Played by hand like <see cref="AnSdkTransferWithAnIdAboveIntMaxValueRecordsTheFileArriving"/>.
    /// Both events carry the running transfer's id, as <c>event_cb</c> adds it to every event while
    /// one runs (__init__.py:320-321).
    /// </remarks>
    [Fact]
    public async Task AnSdkDownloadRefusedAfterItsAcceptanceIsNotRecordedAsTaken()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);
        const uint runningTransferId = 3_000_000_000;

        await PostAsSdkAsync(printer, "/p/telemetry", identity, token, """{"state":"IDLE"}""");

        (HSFile file, StoredFile stored) = await StoreFileAsync(userId, "sdk-busy.gcode");
        Task<DirectSendResult> sending = SendDirectAsync(printerId, file, stored, userId);

        (uint commandId, JsonElement _) = await CollectCommandAsync(printer, identity, token);

        // Act
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"ACCEPTED","source":"CONNECT","data":{},"command_id":{{commandId}},"transfer_id":{{runningTransferId}},"state":"IDLE"}""");
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"REJECTED","source":"CONNECT","data":{},"command_id":{{commandId}},"transfer_id":{{runningTransferId}},"reason":"Another transfer in progress","state":"IDLE"}""");

        DirectSendResult result = await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        result.Sent!.Outcome!.EventType.Should().Be(PrinterEventType.Rejected, "ACCEPTED said only that the printer had started on it");
        result.Sent.Outcome.Reason.Should().Be("Another transfer in progress");

        using IServiceScope scope = _factory.Services.CreateScope();

        long? transferCommandId = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                             .FilesOnPrinters
                                             .Where(row => row.PrinterId == printerId && row.FileId == file.Id)
                                             .Select(row => row.TransferCommandId)
                                             .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

        transferCommandId.Should().BeNull("a refused download is no transfer for a terminal event to name");
        _logs.Failures.Should().BeEmpty();
    }

    /// <summary>
    /// A command a Python-SDK printer accepts, runs, and then fails is answered with the failure. In
    /// between, its telemetry names the command (__init__.py:340-341), which is what keeps it running
    /// here. PrusaLink's pause is the real case: it waits up to fifteen seconds for the printer to
    /// pause, and fails the command if it does not (<c>TryUntilState</c>, command_handlers.py:115-185).
    /// </summary>
    /// <remarks>Played by hand like <see cref="AnSdkTransferWithAnIdAboveIntMaxValueRecordsTheFileArriving"/>.</remarks>
    [Fact]
    public async Task AnSdkCommandThatFailsAfterRunningIsAnsweredWithTheFailure()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        await PostAsSdkAsync(printer, "/p/telemetry", identity, token, """{"state":"PRINTING"}""");

        using IServiceScope scope = _factory.Services.CreateScope();
        Task<CommandOutcome?> pausing = scope.ServiceProvider.GetRequiredService<PrinterCommandService>()
                                             .SendCommandAsync(printerId, new PrusaConnect.Commands.PausePrint(), Caller.Unscoped(userId), TestContext.Current.CancellationToken);

        (uint commandId, JsonElement _) = await CollectCommandAsync(printer, identity, token);

        // Act
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"ACCEPTED","source":"CONNECT","data":{},"command_id":{{commandId}},"state":"PRINTING"}""");
        await PostAsSdkAsync(printer, "/p/telemetry", identity, token,
                             $$"""{"command_id":{{commandId}},"state":"PRINTING"}""");
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"FAILED","source":"WUI","data":{"error":"CommandFailed(\"Couldn't get to any of PAUSED states.\")"},"command_id":{{commandId}},"reason":"Command error","state":"PRINTING"}""");

        CommandOutcome? outcome = await pausing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        outcome!.EventType.Should().Be(PrinterEventType.Failed, "the telemetry still named the command, so it was still running");
        outcome.Reason.Should().Be("Command error");
        _logs.Failures.Should().BeEmpty();
    }

    /// <summary>
    /// An unload on a Python-SDK printer goes out as the SDK's own <c>UNLOAD_FILAMENT</c>, at the chosen
    /// filament's printing temperature and the bed's own target, is answered when the printer takes
    /// it, and is followed once it ends by the nozzle put back to the target it had.
    /// </summary>
    /// <remarks>
    /// PrusaLink reads both kwargs by key (<c>FilamentCommand</c>, command_handlers.py:420-421) and
    /// answers only once <c>M702</c> is done, minutes later - so the caller has its answer at the
    /// <c>ACCEPTED</c>. The MK3's <c>M702</c> leaves the nozzle hot, which is what the follow-up is for.
    /// Played by hand like <see cref="AnSdkTransferWithAnIdAboveIntMaxValueRecordsTheFileArriving"/>.
    /// </remarks>
    [Fact]
    public async Task AnSdkUnloadIsSentAsItsOwnCommandAndPutsTheNozzleBackWhenItEnds()
    {
        // Arrange
        StartWithRealDispatcher();

        (PrinterIdentity identity, string token, int printerId, long userId) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        using HttpClient printer = PrinterListener.CreateClient(_factory);

        // A warm bed and a cold nozzle - the bed is left as it is, and the nozzle cooled down again.
        await PostAsSdkAsync(printer, "/p/telemetry", identity, token, """{"target_nozzle":0.0,"target_bed":60.0,"state":"IDLE"}""");
        await WaitForStatusAsync(printerId, PrinterStatus.Idle);

        FilamentPreset pla = FilamentPreset.Find(model: null, "PLA")!;

        using IServiceScope scope = _factory.Services.CreateScope();
        Task<UnloadOutcome> unloading = scope.ServiceProvider.GetRequiredService<PrinterFilamentService>()
                                             .UnloadAsync(printerId, Caller.Unscoped(userId), toolNumber: null, pla,
                                                          TestContext.Current.CancellationToken);

        (uint commandId, JsonElement command) = await CollectCommandAsync(printer, identity, token);

        // Act
        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"ACCEPTED","source":"CONNECT","data":{},"command_id":{{commandId}},"state":"IDLE"}""");

        UnloadOutcome outcome = await unloading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await PostAsSdkAsync(printer, "/p/events", identity, token,
                             $$"""{"event":"FINISHED","source":"CONNECT","data":{},"command_id":{{commandId}},"state":"IDLE"}""");

        (uint _, string followUpType, string followUp) = await CollectRawCommandAsync(printer, identity, token);

        // Assert
        command.GetProperty("command").GetString().Should().Be("UNLOAD_FILAMENT");
        command.GetProperty("kwargs").GetProperty("nozzle_temperature").GetInt32().Should().Be(pla.NozzleTemperature);
        command.GetProperty("kwargs").GetProperty("bed_temperature").GetInt32().Should().Be(60, "the bed keeps the target it had");

        followUpType.Should().Be("text/x.gcode", "how the SDK tells gcode from a JSON command (__init__.py:683)");
        followUp.Should().Be("M104 S0", "the nozzle was cold before the unload heated it");

        outcome.Answer!.EventType.Should().Be(PrinterEventType.Accepted);
        outcome.NozzleTemperature.Should().Be(pla.NozzleTemperature);
        _logs.Failures.Should().BeEmpty();
    }

    /// <summary>Waits until the printer's stored live state reads <paramref name="status"/>.</summary>
    private async Task WaitForStatusAsync(int printerId, PrinterStatus status)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (true)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            PrinterStatus? stored = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                               .PrinterLiveStates
                                               .Where(row => row.PrinterId == printerId)
                                               .Select(row => (PrinterStatus?)row.Status)
                                               .SingleOrDefaultAsync(timeout.Token);

            if (stored == status)
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    /// <summary>Posts as the Python SDK does, and expects the message accepted.</summary>
    private static async Task PostAsSdkAsync(HttpClient printer, string route, PrinterIdentity identity, string token, string body)
    {
        using HttpRequestMessage request = PostAsSdk(route, identity, token, body);
        using HttpResponseMessage response = await printer.SendAsync(request, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue($"the SDK drops a message it is refused, and this one was answered {response.StatusCode}: {body}");
    }

    /// <summary>
    /// Polls with telemetry, as the SDK does, until a response carries a command, and returns it with
    /// the id its <c>Command-Id</c> header gives it (__init__.py:654-660).
    /// </summary>
    private static async Task<(uint commandId, JsonElement command)> CollectCommandAsync(HttpClient printer, PrinterIdentity identity, string token)
    {
        (uint commandId, string _, string body) = await CollectRawCommandAsync(printer, identity, token);

        using JsonDocument document = JsonDocument.Parse(body);

        return (commandId, document.RootElement.Clone());
    }

    /// <summary>
    /// As <see cref="CollectCommandAsync"/>, for a command whose body may be gcode rather than JSON:
    /// its id, its content type and its body as sent.
    /// </summary>
    private static async Task<(uint commandId, string contentType, string body)> CollectRawCommandAsync(HttpClient printer, PrinterIdentity identity, string token)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        while (true)
        {
            using HttpRequestMessage request = PostAsSdk("/p/telemetry", identity, token, """{"state":"IDLE"}""");
            using HttpResponseMessage response = await printer.SendAsync(request, timeout.Token);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                uint commandId = uint.Parse(response.Headers.GetValues(Headers.CommandId).Single(), CultureInfo.InvariantCulture);

                return (commandId,
                        response.Content.Headers.ContentType?.MediaType ?? string.Empty,
                        await response.Content.ReadAsStringAsync(timeout.Token));
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>A POST with the Python SDK's four headers (<c>make_headers</c>, __init__.py:269-288).</summary>
    private static HttpRequestMessage PostAsSdk(string route, PrinterIdentity identity, string token, string body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, route)
        {
            Content = JsonBody(body),
        };

        request.Headers.Add(Headers.Fingerprint, identity.HeaderFingerprint);
        request.Headers.Add(Headers.Token, token);
        request.Headers.TryAddWithoutValidation("User-Agent", "Prusa-Connect-SDK-Printer/0.8.1");
        request.Headers.Add(Headers.UserAgentPrinter, "1.3.1");
        request.Headers.Add(Headers.UserAgentVersion, "0.8.1");

        return request;
    }

    private async Task<(HSFile file, StoredFile stored)> StoreFileAsync(long userId, string name)
    {
        string path = Path.Combine(_scratch.Path, name);

        await File.WriteAllTextAsync(path, "G28 ; home\n", TestContext.Current.CancellationToken);

        using IServiceScope scope = _factory.Services.CreateScope();
        HomespoolDbContext context = scope.ServiceProvider.GetRequiredService<HomespoolDbContext>();

        HSFile file = new()
        {
            Type = FileType.GCode,
            UserId = userId,
            Name = name,
            Size = new FileInfo(path).Length,
            UploadedAt = DateTimeOffset.UtcNow,
        };

        context.Files.Add(file);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (file, new StoredFile(name, path, file.Size, file.UploadedAt));
    }

    private async Task<DirectSendResult> SendDirectAsync(int printerId, HSFile file, StoredFile stored, long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        Printer printer = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                     .Printers.SingleAsync(candidate => candidate.Id == printerId, TestContext.Current.CancellationToken);

        return await scope.ServiceProvider.GetRequiredService<TransferService>()
                          .SendDirectAsync(printer, file, stored, Caller.Unscoped(userId), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// When the row says <paramref name="fileId"/> arrived, settling until it does or ten seconds
    /// pass - the event reaches the log through the telemetry writer's batches, not at once.
    /// </summary>
    private async Task<DateTimeOffset?> ArrivedAtAsync(int printerId, long fileId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TransferService transfers = _factory.Services.GetRequiredService<TransferService>();

        while (!timeout.IsCancellationRequested)
        {
            await transfers.SettleAsync(printerId, TestContext.Current.CancellationToken);

            using IServiceScope scope = _factory.Services.CreateScope();

            DateTimeOffset? arrivedAt = await scope.ServiceProvider.GetRequiredService<HomespoolDbContext>()
                                                   .FilesOnPrinters
                                                   .Where(row => row.PrinterId == printerId && row.FileId == fileId)
                                                   .Select(row => row.ArrivedAt)
                                                   .SingleOrDefaultAsync(TestContext.Current.CancellationToken);

            if (arrivedAt is not null)
            {
                return arrivedAt;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
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

    /// <summary>Another connection holding the printer's identity, reduced to what the registry asks of one.</summary>
    private sealed class StandInLink : IPrinterLink
    {
        public bool Completed { get; private set; }

        public bool IsOpen => !Completed;

        public TransferSlot TransferSlot => TransferSlot.Free;

        public Task<CommandSendResult> SendAsync(IPrinterIntent intent, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Nothing sends to the stand-in in this test.");
        }

        public void Complete()
        {
            Completed = true;
        }
    }

    /// <summary>
    /// Creates actors that are displaced by a stand-in the first time anything is posted to one of
    /// them, after the session has handed the actor out and before the post reaches its mailbox.
    /// </summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Constructed by the container, in place of the factory it derives from.")]
    private sealed class DisplacedOnFirstPostActorFactory : PrinterConnectionActorFactory
    {
        private readonly PrinterConnectionRegistry _registry;
        private int _armed = 1;

        public DisplacedOnFirstPostActorFactory(ITelemetrySink sink,
                                                ILogger<PrinterConnectionActor> logger,
                                                IOptionsMonitor<PrusaConnectOptions> options,
                                                ITransferContentStore contentStore,
                                                PrinterTrafficLog trafficLog,
                                                PrinterConnectionRegistry registry)
            : base(sink, logger, options, contentStore, trafficLog)
        {
            _registry = registry;
        }

        public override IPrinterConnectionActor Create(int printerId, IPrinterConnection connection)
        {
            return new DisplacedOnPostActor(base.Create(printerId, connection), () =>
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    _registry.Register(printerId, new StandInLink(), overPlaintext: false);
                }
            });
        }
    }

    /// <summary>A real actor that runs <c>beforePost</c> ahead of every post.</summary>
    private sealed class DisplacedOnPostActor : IPrinterConnectionActor
    {
        private readonly IPrinterConnectionActor _inner;
        private readonly Action _beforePost;

        public DisplacedOnPostActor(IPrinterConnectionActor inner, Action beforePost)
        {
            _inner = inner;
            _beforePost = beforePost;
        }

        public Task Completion => _inner.Completion;

        public bool CanStreamChunks => _inner.CanStreamChunks;

        public PrinterClient Client => _inner.Client;

        public PrinterDialect Dialect => _inner.Dialect;

        public bool IsOpen => _inner.IsOpen;

        public TransferSlot TransferSlot => _inner.TransferSlot;

        public ValueTask PostAsync(ConnectionMessage message, CancellationToken cancellationToken)
        {
            _beforePost();

            return _inner.PostAsync(message, cancellationToken);
        }

        public Task<CommandSendResult> SendCommandAsync(ISendableCommand command, CancellationToken cancellationToken)
        {
            return _inner.SendCommandAsync(command, cancellationToken);
        }

        public Task<CommandSendResult> SendAsync(IPrinterIntent intent, CancellationToken cancellationToken)
        {
            return _inner.SendAsync(intent, cancellationToken);
        }

        public void Complete()
        {
            _inner.Complete();
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
