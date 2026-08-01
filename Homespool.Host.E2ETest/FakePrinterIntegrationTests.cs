using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Controllers;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The FakePrinter driven against the full pipeline - auth handler, WebSocket handler, dispatcher,
/// actor, telemetry writer, real SQLite. Covers what one physical printer cannot: capture replay
/// into persisted rows without hardware, reconnect churn, and the command-path misbehaviours a
/// healthy printer refuses to produce (<c>notes/fake-printer-harness.md</c>).
/// </summary>
/// <remarks>
/// The command response timeout is dropped to 2 s via options so the timeout tests cost seconds,
/// not the default 10 each. The library deliberately shares no code with the server (it builds its
/// JSON from firmware source, not our DTOs), so every green assertion here is a genuine
/// cross-check of two independent readings of the protocol.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class FakePrinterIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-e2e-fake-{Guid.NewGuid():N}.db");
    private readonly CapturingSink _logs = new();
    private readonly List<string> _offerDirectories = [];
    private HomespoolFactory _root = null!;
    private WebApplicationFactory<PrinterAppController> _factory = null!;

    public Task InitializeAsync()
    {
        _root = new HomespoolFactory($"Data Source={_databasePath}", null, _logs);
        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<PrusaConnectOptions>(options => options.CommandResponseTimeoutSeconds = 2)));

        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _root.Dispose();

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        foreach (string directory in _offerDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Replaying the committed capture lands every telemetry message as a persisted
    /// <see cref="TelemetrySample"/> and every event as a <see cref="PrinterEvent"/> - the first
    /// end-to-end persistence proof that needs no hardware. Before this, only the MK3.5 sessions
    /// had ever exercised the whole chain at once.
    /// </summary>
    [Fact]
    public async Task CaptureReplayPersistsEveryMessageThroughTheFullPipeline()
    {
        (PrinterIdentity identity, string token, int printerId, long _) = await EnrolNewPrinterAsync();

        // 1 ms pacing keeps the writer's drop-oldest channel (4 batches of headroom) far from
        // engaging, so an exact row count is a fair assertion rather than a race.
        CaptureReplaySource source = new("websocket.capture", TimeSpan.FromMilliseconds(1));
        await using FakePrinterClient fake = new(identity, TimeProvider.System, new FakePrinterOptions { TelemetrySource = source });
        fake.Token = token;

        await fake.ConnectAsync(ConnectViaTestServerAsync);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(120));
        Task run = fake.RunAsync(cancellation.Token);

        await fake.TelemetryCompleted.WaitAsync(TimeSpan.FromSeconds(90));

        // The capture holds 2058 telemetry documents and 5 events (CaptureReplayTests' counts);
        // the fake's own connect-time INFO makes it 6 events.
        int samples = await WaitForCountAsync(
            context => context.TelemetrySamples.CountAsync(s => s.PrinterId == printerId),
            atLeast: 2058);
        int events = await WaitForCountAsync(
            context => context.PrinterEvents.CountAsync(e => e.PrinterId == printerId),
            atLeast: 6);

        samples.Should().Be(2058, "every telemetry message in the capture becomes exactly one dense sample");
        events.Should().Be(6, "five capture events plus the connect-time INFO");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
            PrinterLiveState liveState = await context.PrinterLiveStates.SingleAsync(l => l.PrinterId == printerId);
            liveState.LastSeenAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5),
                "the merge ran against real replayed data just now");
        }

        await fake.CloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        fake.ReplyFault.Should().BeNull();
        _logs.Failures.Should().BeEmpty("a faithful replay is not an error path");
    }

    /// <summary>
    /// A command round trip against the firmware-faithful fake: PAUSE while printing answers
    /// FINISHED, correlated to the right command, and the fake's device actually pauses.
    /// </summary>
    [Fact]
    public async Task PauseWhilePrintingRoundTripsThePrintersFinished()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            configure: f => f.Device.StartPrint(jobId: 301));

        await using (fake)
        {
            CommandOutcome outcome = await SendCommandAsync(printerId, userId, new PausePrint());

            outcome.EventType.Should().Be(Events.Finished);
            fake.Device.State.Should().Be(DeviceState.Paused, "the command must actually have executed");
            fake.ReceivedCommands.Should().ContainSingle().Which.Kind.Should().Be(ServerCommandKind.Json);

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// The printer's own rejection - its exact reason string included - travels all the way back to
    /// the caller, mirroring what the live MK3.5 demonstrated on 2026-07-24.
    /// </summary>
    [Fact]
    public async Task PauseWhileIdleSurfacesThePrintersOwnRejectionReason()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync();

        await using (fake)
        {
            CommandOutcome outcome = await SendCommandAsync(printerId, userId, new PausePrint());

            outcome.EventType.Should().Be(Events.Rejected);
            outcome.Reason.Should().Be("No print to pause", "the JC macro's reason string must reach the caller verbatim");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// A printer that goes silent on a command surfaces as the response timeout - the path that
    /// has never executed against hardware, because the real MK3.5 always answers.
    /// </summary>
    [Fact]
    public async Task ASilentPrinterSurfacesAsResponseTimedOut()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            options: new FakePrinterOptions { Policy = new NoReplyPolicy() });

        await using (fake)
        {
            Func<Task> act = async () => await SendCommandAsync(printerId, userId, new PausePrint());

            await act.Should().ThrowAsync<CommandResponseTimedOutException>();
            fake.ReceivedCommands.Should().ContainSingle("the frame reached the printer; only the answer is missing");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// An ack bearing the wrong command id must be ignored - the command still times out rather
    /// than completing against a stray reply, and nothing faults server-side.
    /// </summary>
    [Fact]
    public async Task AStrayAckWithTheWrongCommandIdIsIgnored()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            configure: f => f.Device.StartPrint(jobId: 1),
            policyFactory: identity => new WrongCommandIdPolicy(new FirmwareFaithfulPolicy(identity, TimeProvider.System)));

        await using (fake)
        {
            Func<Task> act = async () => await SendCommandAsync(printerId, userId, new PausePrint());

            await act.Should().ThrowAsync<CommandResponseTimedOutException>(
                "a reply with a mismatched command_id must never complete the pending command");
            _logs.Failures.Should().BeEmpty("ignoring a stray ack is not an error path");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>Two acks for one command: the first completes it, the second is survived.</summary>
    [Fact]
    public async Task ADoubledAckCompletesOnceAndBreaksNothing()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            configure: f => f.Device.StartPrint(jobId: 1),
            policyFactory: identity => new DoubleReplyPolicy(new FirmwareFaithfulPolicy(identity, TimeProvider.System)));

        await using (fake)
        {
            CommandOutcome first = await SendCommandAsync(printerId, userId, new PausePrint());
            first.EventType.Should().Be(Events.Finished);

            // The connection must still be fully usable after the surplus ack arrived.
            CommandOutcome second = await SendCommandAsync(printerId, userId, new ResumePrint());
            second.EventType.Should().Be(Events.Finished);

            _logs.Failures.Should().BeEmpty("a surplus ack is survived, not treated as a fault");

            await EndRunAsync(fake, run);
        }
    }

    /// <summary>
    /// The printer dying mid-command - socket aborted, no answer, no close handshake - fails the
    /// pending command as not-connected rather than hanging until the response timeout.
    /// </summary>
    [Fact]
    public async Task ADisconnectMidCommandFailsAsNotConnected()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            options: new FakePrinterOptions { Policy = new DisconnectOnCommandPolicy() });

        await using (fake)
        {
            Func<Task> act = async () => await SendCommandAsync(printerId, userId, new PausePrint());

            await act.Should().ThrowAsync<PrinterNotConnectedException>(
                "teardown must fail the pending command, not leave it to the timeout");

            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Reconnect churn - impossible to produce with one physical printer - leaves the registry
    /// consistent: after five connect/close cycles the sixth connection still round-trips a
    /// command, i.e. no stale connection's teardown removed the live one.
    /// </summary>
    [Fact]
    public async Task FiveReconnectCyclesLeaveTheRegistryServingTheLiveConnection()
    {
        (PrinterIdentity identity, string token, int printerId, long userId) = await EnrolNewPrinterAsync();

        for (int cycle = 0; cycle < 5; cycle++)
        {
            await using FakePrinterClient fake = new(identity, TimeProvider.System);
            fake.Token = token;

            await fake.ConnectAsync(ConnectViaTestServerAsync);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
            Task run = fake.RunAsync(cancellation.Token);
            await WaitUntilConnectedAsync(printerId);

            await fake.CloseAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using FakePrinterClient last = new(identity, TimeProvider.System);
        last.Token = token;
        last.Device.StartPrint(jobId: 1);

        await last.ConnectAsync(ConnectViaTestServerAsync);
        using CancellationTokenSource lastCancellation = new(TimeSpan.FromSeconds(30));
        Task lastRun = last.RunAsync(lastCancellation.Token);
        await WaitUntilConnectedAsync(printerId);

        CommandOutcome outcome = await SendCommandAsync(printerId, userId, new PausePrint());

        outcome.EventType.Should().Be(Events.Finished, "the surviving registration must be the live socket");
        _logs.Failures.Should().BeEmpty("reconnect churn is ordinary printer behaviour, not an error path");

        await EndRunAsync(last, lastRun);
    }

    /// <summary>
    /// A whole file crosses the wire and arrives byte-for-byte, driven by two independent readings of
    /// the protocol: the server's chunk server and the fake's download engine, neither built from the
    /// other's code. 400 KiB of bgcode takes the generic order, so this is the plain sequential case.
    /// </summary>
    [Fact]
    public async Task AFileTransfersEndToEndWithItsBytesIntact()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync();
        byte[] content = Content(400 * 1024);
        string hash = Offer(content, "model.bgcode");

        CommandOutcome outcome = await SendCommandAsync(printerId, userId, new StartConnectDownload
        {
            Path = "/usb/model.bgcode",
            Hash = hash,
            TeamId = 1,
            OriginalSize = content.Length,
        });

        outcome.EventType.Should().Be(Events.TransferInfo, "a download is acknowledged with TRANSFER_INFO");

        FakeTransfer transfer = await WaitForTransferAsync(fake);

        transfer.IsComplete.Should().BeTrue();
        transfer.Content.ToArray().Should().Equal(content);
        transfer.NegotiationCount.Should().Be(1);
        _logs.Failures.Should().BeEmpty();

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// The case that was broken in production once: plain gcode over 512 KiB fetches its tail first
    /// and renegotiates with a fresh <c>file_id</c> mid-transfer. The server bound the id only on the
    /// first request and ignored the jump, so the most ordinary file there is would have hung forever -
    /// the inline engine has no stall timeout. Here the fake performs the jump of its own accord,
    /// because it models the download order rather than being told to.
    /// </summary>
    [Fact]
    public async Task ALargePlainGcodeRangeJumpsAndStillArrivesIntact()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync();
        byte[] content = Content(600 * 1024);
        string hash = Offer(content, "model.gcode");

        await SendCommandAsync(printerId, userId, new StartConnectDownload
        {
            Path = "/usb/model.gcode",
            Hash = hash,
            TeamId = 1,
            OriginalSize = content.Length,
        });

        FakeTransfer transfer = await WaitForTransferAsync(fake);

        transfer.NegotiationCount.Should().Be(2, "a RangeJump renegotiates from scratch");
        transfer.IsComplete.Should().BeTrue();
        transfer.Content.ToArray().Should().Equal(content, "the two negotiations must tile the file exactly");
        _logs.Failures.Should().BeEmpty();

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A hash the server has forgotten - the shape of a printer resuming across a restart - is failed
    /// deliberately with the zero-length chunk, and the printer ends the transfer rather than waiting
    /// forever. The absence of a stall timeout in the inline engine is what makes "always answer"
    /// load-bearing rather than merely tidy.
    /// </summary>
    [Fact]
    public async Task AnUnknownHashEndsTheTransferInsteadOfHangingIt()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync();

        await SendCommandAsync(printerId, userId, new StartConnectDownload
        {
            Path = "/usb/missing.bgcode",
            Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAA",
            TeamId = 1,
            OriginalSize = 4096,
        });

        FakeTransfer transfer = await WaitForTransferAsync(fake);

        transfer.HasFailed.Should().BeTrue("an empty chunk is the server's 'I failed' signal");
        transfer.IsComplete.Should().BeFalse();

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A command sent while a transfer is running is answered normally. Chunks bypass the one-in-flight
    /// command guard on the printer (connect.cpp:468), so the two streams share the socket without
    /// blocking each other - and on the server side both are messages to the same single-threaded
    /// actor, which is what keeps them from interleaving mid-message.
    /// </summary>
    [Fact]
    public async Task ATransferAndACommandShareTheConnection()
    {
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            configure: client => client.Device.StartPrint(jobId: 77));
        byte[] content = Content(600 * 1024);
        string hash = Offer(content, "while-printing.gcode");

        await SendCommandAsync(printerId, userId, new StartConnectDownload
        {
            Path = "/usb/while-printing.gcode",
            Hash = hash,
            TeamId = 1,
            OriginalSize = content.Length,
        });

        CommandOutcome outcome = await SendCommandAsync(printerId, userId, new PausePrint());
        outcome.EventType.Should().Be(Events.Finished, "a command must not be starved by a running transfer");

        FakeTransfer transfer = await WaitForTransferAsync(fake);
        transfer.Content.ToArray().Should().Equal(content, "and the transfer must not be disturbed by the command");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A printer that drops mid-transfer and reconnects starts over with a fresh <c>file_id</c>, and
    /// the server serves the new transfer rather than the abandoned one. The dropped transfer's file
    /// handle dies with the connection.
    /// </summary>
    [Fact]
    public async Task ATransferSurvivesTheConnectionDroppingUnderIt()
    {
        (PrinterIdentity identity, string token, int printerId, long userId) = await EnrolNewPrinterAsync();
        byte[] content = Content(600 * 1024);
        string hash = Offer(content, "resumed.gcode");

        StartConnectDownload command = new()
        {
            Path = "/usb/resumed.gcode",
            Hash = hash,
            TeamId = 1,
            OriginalSize = content.Length,
        };

        await using (FakePrinterClient dropped = new(identity, TimeProvider.System))
        {
            dropped.Token = token;
            await dropped.ConnectAsync(ConnectViaTestServerAsync);
            Task droppedRun = dropped.RunAsync(CancellationToken.None);
            await WaitUntilConnectedAsync(printerId);

            await SendCommandAsync(printerId, userId, command);
            await dropped.CloseAsync();
            await droppedRun.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using FakePrinterClient reconnected = new(identity, TimeProvider.System);
        reconnected.Token = token;
        await reconnected.ConnectAsync(ConnectViaTestServerAsync);
        Task run = reconnected.RunAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(printerId);

        await SendCommandAsync(printerId, userId, command);

        FakeTransfer transfer = await WaitForTransferAsync(reconnected);

        transfer.IsComplete.Should().BeTrue("the offer outlives the connection, so a restarted transfer completes");
        transfer.Content.ToArray().Should().Equal(content);

        await EndRunAsync(reconnected, run);
    }

    private static byte[] Content(int length)
    {
        // Position-dependent, so a chunk delivered at the wrong offset fails on content rather than
        // passing because every byte looked the same.
        byte[] content = new byte[length];

        for (int i = 0; i < length; i++)
        {
            content[i] = (byte)((i * 31) % 251);
        }

        return content;
    }

    private static async Task EndRunAsync(FakePrinterClient fake, Task run)
    {
        await fake.CloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        fake.ReplyFault.Should().BeNull("a faulted fake would invalidate what this test claims about the server");
    }

    /// <summary>
    /// Writes the bytes to a temp file and offers them under a fresh hash, the way
    /// <c>PrinterController</c> does after an upload.
    /// </summary>
    private string Offer(byte[] content, string fileName)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ps-e2e-offer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _offerDirectories.Add(directory);

        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, content);

        string hash = Guid.NewGuid().ToString("N")[..27];
        _factory.Services.GetRequiredService<ITransferOffers>().Offer(hash, path);

        return hash;
    }

    /// <summary>
    /// Waits for the fake's transfer to reach a terminal state. The transfer is driven entirely by the
    /// two ends talking to each other, so there is no single await a test could hold instead.
    /// </summary>
    private async Task<FakeTransfer> WaitForTransferAsync(FakePrinterClient fake)
    {
        bool ended = await WaitUntilAsync(
            () => Task.FromResult(fake.Device.LastTransfer is not null),
            TimeSpan.FromSeconds(30));

        ended.Should().BeTrue("the transfer never reached a terminal state - the inline engine has no "
                            + "stall timeout, so a hang here is the production symptom");
        fake.ReplyFault.Should().BeNull();

        return fake.Device.LastTransfer!;
    }

    /// <summary>
    /// A printer polling and registering at its natural rate is never rate-limited. The limits on
    /// <c>/p/register</c> exist because it is anonymous and internet-reachable (it creates a database
    /// row per POST and is a guessing oracle on GET), but rejecting a real printer is expensive: the
    /// firmware treats any non-2xx as <c>OnlineError::Server</c> and burns one of only three POST
    /// retries before abandoning registration for good.
    /// </summary>
    /// <remarks>
    /// Deliberately a rate test, not a limit test: it asserts that ordinary behaviour stays under the
    /// ceiling, which is the property that matters and the one a future tightening of the limits would
    /// break. Sends well past a single printer's per-minute traffic (one POST, then polls at 12/min).
    /// </remarks>
    [Fact]
    public async Task APrinterRegisteringAndPollingAtItsNaturalRateIsNeverRateLimited()
    {
        // Arrange
        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        await using FakePrinterClient fake = new(identity, TimeProvider.System);
        using HttpClient anonymous = PrinterListener.CreateClient(_factory);

        // Act - one register, then far more polls than a real printer manages in a minute.
        string code = await fake.RegisterAsync(anonymous);

        for (int i = 0; i < 40; i++)
        {
            string? token = await fake.PollForTokenOnceAsync(anonymous, code);
            token.Should().BeNull("nothing has claimed this registration, so every poll is a pending 202");
        }

        // Assert - a 429 anywhere above would have surfaced as an exception from
        // EnsureSuccessStatusCode inside the helpers; assert the endpoint is still serving normally.
        string second = await fake.RegisterAsync(anonymous);

        second.Should().NotBeNullOrEmpty("the endpoint must still answer a printer after a minute's worth of polling");
    }

    /// <summary>
    /// A scheduled filament change survives the whole path - fake, wire, dispatcher, merge, database -
    /// landing in both <see cref="PrinterLiveState"/> and the dense <see cref="TelemetrySample"/>.
    /// </summary>
    /// <remarks>
    /// Before this, <c>TimeToFilamentChange</c> was parsed, merged and persisted by code no test
    /// touched anywhere: the firmware emits <c>filament_change_in</c> only while a pause is scheduled
    /// (render.cpp:164), so the committed capture holds none and a hardware session would need a print
    /// with a deliberate M600 in it. The fake is the only practical way to exercise it - which is
    /// exactly what a fake is for.
    /// </remarks>
    [Fact]
    public async Task AScheduledFilamentChangeReachesLiveStateAndTheSample()
    {
        // Arrange
        (PrinterIdentity identity, string token, int printerId, long _) = await EnrolNewPrinterAsync();

        SyntheticTelemetrySource source = new()
        {
            PrintingInterval = TimeSpan.FromMilliseconds(50),
            IdleInterval = TimeSpan.FromMilliseconds(50),
            Readings = new TelemetryReadings(TimeToFilamentChange: 300),
        };

        await using FakePrinterClient fake = new(identity, TimeProvider.System, new FakePrinterOptions { TelemetrySource = source });
        fake.Token = token;
        fake.Device.StartPrint(jobId: 77);

        await fake.ConnectAsync(ConnectViaTestServerAsync);
        Task run = fake.RunAsync(CancellationToken.None);

        // Act
        bool persisted = await WaitUntilAsync(async () =>
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();

            return await context.TelemetrySamples
                .AnyAsync(sample => sample.PrinterId == printerId && sample.TimeToFilamentChange == 300);
        }, TimeSpan.FromSeconds(20));

        // Assert
        persisted.Should().BeTrue("the countdown must reach a dense sample, not be dropped in the merge");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
            PrinterLiveState liveState = await context.PrinterLiveStates.SingleAsync(l => l.PrinterId == printerId);

            liveState.TimeToFilamentChange.Should().Be(300,
                "the live view is what a UI reads, so the merge has to keep it");
        }

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// The storage listing, whole: an HTTP GET becomes a <c>SEND_FILE_INFO</c> to a live printer, its
    /// answering <c>FILE_INFO</c> is parsed out of the command's own reply, and the entries come back
    /// in this API's vocabulary rather than firmware's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the first command whose <i>payload</i> is the answer, so it exercises a path nothing
    /// else does: <c>CommandOutcome&lt;T&gt;</c> carrying a deserialised body up through
    /// <c>AskAsync</c>. A job-control verb would still pass with the payload thrown away.
    /// </para>
    /// <para>
    /// <b>What this does not prove: 8.3 aliasing.</b> The fake has no FAT filesystem, so its short
    /// and long names coincide - see <c>FakeStorage</c>'s remarks. Hardware aliases nearly every
    /// entry, and that case is covered by replaying a real capture in the host's
    /// <c>CaptureReplayTests</c> instead. The two tests are complements, and neither is sufficient.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheStorageListingReachesAConnectedPrinterAndComesBackInOurVocabulary()
    {
        // Arrange
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            configure: f =>
            {
                f.Device.Storage.AddFile("/usb/lampshade.gcode", 7647560, 1764804970);
                f.Device.Storage.AddFolder("/usb/sub");

                // One level down, so the listing proves it reports direct children rather than
                // everything on the drive.
                f.Device.Storage.AddFile("/usb/sub/deep.bgcode", 4242, 1764805000);
            });

        (Guid uuid, string token) = await UuidAndTokenAsync(printerId, userId);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/printers/{uuid}/storage/usb");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement listing = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        listing.GetProperty("path").GetString().Should().Be("/usb");
        listing.GetProperty("kind").GetString().Should().Be("folder");

        JsonElement[] entries = listing.GetProperty("entries").EnumerateArray().ToArray();
        entries.Should().HaveCount(2, "the file one level down is not a child of /usb");

        JsonElement file = entries.Single(e => e.GetProperty("kind").GetString() == "printFile");
        file.GetProperty("name").GetString().Should().Be("lampshade.gcode");
        file.GetProperty("size").GetInt64().Should().Be(7647560);

        // Translated out of firmware's Unix seconds, which is the point of having our own DTO.
        file.GetProperty("modifiedAt").GetDateTimeOffset()
            .Should().Be(DateTimeOffset.FromUnixTimeSeconds(1764804970));

        entries.Should().ContainSingle(e => e.GetProperty("kind").GetString() == "folder");

        // And the nested path lists on its own, which is what the catch-all route is for.
        using HttpResponseMessage nested = await client.GetAsync($"/api/v1/printers/{uuid}/storage/usb/sub");

        nested.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonDocument.Parse(await nested.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("entries").EnumerateArray()
                    .Single().GetProperty("name").GetString().Should().Be("deep.bgcode");

        await EndRunAsync(fake, run);
    }

    /// <summary>
    /// A path the printer will not answer for comes back as its own refusal, in a ProblemDetails
    /// carrying firmware's words - not as an invented 404.
    /// </summary>
    /// <remarks>
    /// The API cannot tell "no such path" from any other refusal without matching on that text, so it
    /// does not try; the caller gets the reason and decides. This is also the only end-to-end proof
    /// that a real printer refusal reaches the caller as ProblemDetails rather than as the anonymous
    /// object it used to be.
    /// </remarks>
    [Fact]
    public async Task APathThePrinterRefusesComesBackAsItsOwnReason()
    {
        // Arrange
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync();

        (Guid uuid, string token) = await UuidAndTokenAsync(printerId, userId);

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        using HttpResponseMessage response =
            await client.GetAsync($"/api/v1/printers/{uuid}/storage/usb/nothing-here.gcode");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        JsonElement problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        problem.GetProperty("detail").GetString().Should().Be("File not found",
            "the printer's own words, not ours");
        problem.GetProperty("command").GetString().Should().Be("SEND_FILE_INFO");
        problem.GetProperty("outcome").GetString().Should().Be("Rejected");

        await EndRunAsync(fake, run);
    }

    /// <summary>The printer's uuid and a personal access token for its owner.</summary>
    private async Task<(Guid uuid, string token)> UuidAndTokenAsync(int printerId, long userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
        Guid uuid = (await context.Printers.SingleAsync(p => p.Id == printerId)).Uuid;

        ApiTokenService tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
        (_, string token) = await tokens.CreateAsync(userId, "storage-e2e", CancellationToken.None);

        return (uuid, token);
    }

    private Task<(PrinterIdentity identity, string token, int printerId, long userId)> EnrolNewPrinterAsync()
    {
        return EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);
    }

    /// <summary>
    /// Enrols a fresh printer and starts a connected, running fake for it.
    /// <paramref name="policyFactory"/> exists because policies wrapping
    /// <see cref="FirmwareFaithfulPolicy"/> need the identity, which doesn't exist until this
    /// method creates it; when supplied it wins over <paramref name="options"/>' policy.
    /// </summary>
    private async Task<(FakePrinterClient fake, Task run, int printerId, long userId)> StartConnectedFakeAsync(
        FakePrinterOptions? options = null,
        Action<FakePrinterClient>? configure = null,
        Func<PrinterIdentity, CommandAnswerPolicy>? policyFactory = null)
    {
        (PrinterIdentity identity, string token, int printerId, long userId) = await EnrolNewPrinterAsync();

        FakePrinterOptions effective = policyFactory is null
            ? options ?? new FakePrinterOptions()
            : new FakePrinterOptions { Policy = policyFactory(identity) };

        FakePrinterClient fake = new(identity, TimeProvider.System, effective);
        fake.Token = token;
        configure?.Invoke(fake);

        await fake.ConnectAsync(ConnectViaTestServerAsync);
        Task run = fake.RunAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(printerId);

        return (fake, run, printerId, userId);
    }

    /// <summary>
    /// Drives a job-control verb the way a script does: over HTTP, on a personal access token, against
    /// a printer that is genuinely connected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The six verbs were verified against the real MK3.5 in July, but only through
    /// <c>PrinterCommandService</c> - nothing had ever exercised them as endpoints. This covers what
    /// the route adds on top: binding the uuid, resolving the printer for <em>this</em> caller,
    /// mapping the printer's answer to a status code, and doing it all on a bearer credential rather
    /// than a cookie.
    /// </para>
    /// <para>
    /// A token rather than a cookie deliberately - it is the credential a script would hold, and it
    /// authenticates as the printer's actual owner, which <c>EnrolmentFlowHelper</c>'s separate user
    /// could not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AJobControlVerbReachesAConnectedPrinterOverHttp()
    {
        // Arrange
        // Printing, because PAUSE against an idle printer is legitimately rejected - that path is
        // covered by PauseWhileIdleSurfacesThePrintersOwnRejectionReason.
        (FakePrinterClient fake, Task run, int printerId, long userId) = await StartConnectedFakeAsync(
            configure: f => f.Device.StartPrint(jobId: 512));

        Guid uuid;
        string token;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
            uuid = (await context.Printers.SingleAsync(p => p.Id == printerId)).Uuid;

            ApiTokenService tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            (_, token) = await tokens.CreateAsync(userId, "e2e", CancellationToken.None);
        }

        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/v1/printers/{uuid}/command/pause", content: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "204 means the printer answered and did not refuse");

        fake.Device.State.Should().Be(DeviceState.Paused,
            "the command must have reached the printer and executed, not merely been accepted by the API");

        // A printer nobody may see is indistinguishable from one that does not exist.
        using HttpResponseMessage unknown = await client.PutAsync(
            $"/api/v1/printers/{Guid.NewGuid()}/command/pause", content: null);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await EndRunAsync(fake, run);
    }

    private async Task<CommandOutcome> SendCommandAsync(int printerId, long userId, ISendableCommand command)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        PrinterCommandService service = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

        // Every command these tests send is one the printer answers, so a null here would itself be
        // a failure worth surfacing loudly rather than a case to handle.
        return await service.SendCommandAsync(printerId, command, userId, CancellationToken.None)
            ?? throw new InvalidOperationException($"{command.WireName} reported no answer expected.");
    }

    private async Task<WebSocket> ConnectViaTestServerAsync(FakePrinterConnectRequest request, CancellationToken cancellationToken)
    {
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.SubProtocols.Add(request.SubProtocol);
        wsClient.ConfigureRequest = httpRequest =>
        {
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                httpRequest.Headers[header.Key] = header.Value;
            }
        };

        // The fake's own Uri, but on the printer listener: it is configured with no base address here
        // (TestServer's connector ignores the host), and /p/ws exists on that listener only.
        return await wsClient.ConnectAsync(PrinterListener.WebSocketUri(_factory), cancellationToken);
    }

    private async Task WaitUntilConnectedAsync(int printerId)
    {
        PrinterConnectionRegistry registry = _factory.Services.GetRequiredService<PrinterConnectionRegistry>();

        for (int i = 0; i < 100; i++)
        {
            if (registry.TryGet(printerId, out _))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Printer {printerId} never appeared in the connection registry.");
    }

    /// <summary>
    /// Polls a database predicate until it holds - the writer batches on its own loop, so there is no
    /// moment a test can await directly. Sibling of <see cref="WaitForCountAsync"/> for the cases where
    /// the question is "did this row appear?" rather than "how many are there?".
    /// </summary>
    private async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<int> WaitForCountAsync(Func<HSDbContext, Task<int>> count, int atLeast)
    {
        int seen = 0;

        for (int i = 0; i < 300; i++)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
            seen = await count(context);

            if (seen >= atLeast)
            {
                return seen;
            }

            await Task.Delay(100);
        }

        return seen;
    }
}
