using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Homespool.Data;
using Homespool.FakePrinter;
using Homespool.Host.Controllers;
using Homespool.Host.Exceptions;
using Homespool.Host.PrusaConnect;
using Homespool.Host.PrusaConnect.Commands;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        (PrinterIdentity identity, string token, int printerId, long _) = await EnrollNewPrinterAsync();

        // 1 ms pacing keeps the writer's drop-oldest channel (4 batches of headroom) far from
        // engaging, so an exact row count is a fair assertion rather than a race.
        CaptureReplaySource source = new("websocket.capture", TimeSpan.FromMilliseconds(1));
        await using FakePrinterClient fake = new(identity, new FakePrinterOptions { TelemetrySource = source });
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
            policyFactory: identity => new WrongCommandIdPolicy(new FirmwareFaithfulPolicy(identity)));

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
            policyFactory: identity => new DoubleReplyPolicy(new FirmwareFaithfulPolicy(identity)));

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
        (PrinterIdentity identity, string token, int printerId, long userId) = await EnrollNewPrinterAsync();

        for (int cycle = 0; cycle < 5; cycle++)
        {
            await using FakePrinterClient fake = new(identity);
            fake.Token = token;

            await fake.ConnectAsync(ConnectViaTestServerAsync);
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
            Task run = fake.RunAsync(cancellation.Token);
            await WaitUntilConnectedAsync(printerId);

            await fake.CloseAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using FakePrinterClient last = new(identity);
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

    private static async Task EndRunAsync(FakePrinterClient fake, Task run)
    {
        await fake.CloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        fake.ReplyFault.Should().BeNull("a faulted fake would invalidate what this test claims about the server");
    }

    private async Task<(PrinterIdentity identity, string token, int printerId, long userId)> EnrollNewPrinterAsync()
    {
        PrinterIdentity identity = PrinterIdentity.CreateRandom();
        await using FakePrinterClient enrolling = new(identity);
        using HttpClient anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        string code = await enrolling.RegisterAsync(anonymous);

        (HSUser user, HttpClient appClient) = await EnrollmentFlowHelper.CreateAuthenticatedUserAsync(
            _factory, $"{identity.HeaderFingerprint}@example.com");

        using (appClient)
        {
            HttpResponseMessage claim = await appClient.PostAsJsonAsync(
                "/api/v1/printers/register",
                new { name = "Fake printer", location = "Test bench", code });
            claim.EnsureSuccessStatusCode();
        }

        string? token = await enrolling.PollForTokenOnceAsync(anonymous, code);
        token.Should().NotBeNull("the claim just happened, so the poll must redeem the code");

        using IServiceScope scope = _factory.Services.CreateScope();
        HSDbContext context = scope.ServiceProvider.GetRequiredService<HSDbContext>();
        PrusaConnectAuthenticationData auth = await context.PrusaConnectAuthentication
            .Include(a => a.Printer)
            .SingleAsync(a => a.FingerPrintKey == PrinterFingerprint.Key(identity.Fingerprint));

        return (identity, token!, auth.Printer!.Id, user.Id);
    }

    /// <summary>
    /// Enrolls a fresh printer and starts a connected, running fake for it.
    /// <paramref name="policyFactory"/> exists because policies wrapping
    /// <see cref="FirmwareFaithfulPolicy"/> need the identity, which doesn't exist until this
    /// method creates it; when supplied it wins over <paramref name="options"/>' policy.
    /// </summary>
    private async Task<(FakePrinterClient fake, Task run, int printerId, long userId)> StartConnectedFakeAsync(
        FakePrinterOptions? options = null,
        Action<FakePrinterClient>? configure = null,
        Func<PrinterIdentity, CommandAnswerPolicy>? policyFactory = null)
    {
        (PrinterIdentity identity, string token, int printerId, long userId) = await EnrollNewPrinterAsync();

        FakePrinterOptions effective = policyFactory is null
            ? options ?? new FakePrinterOptions()
            : new FakePrinterOptions { Policy = policyFactory(identity) };

        FakePrinterClient fake = new(identity, effective);
        fake.Token = token;
        configure?.Invoke(fake);

        await fake.ConnectAsync(ConnectViaTestServerAsync);
        Task run = fake.RunAsync(CancellationToken.None);
        await WaitUntilConnectedAsync(printerId);

        return (fake, run, printerId, userId);
    }

    private async Task<CommandOutcome> SendCommandAsync(int printerId, long userId, ISendableCommand command)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        PrinterCommandService service = scope.ServiceProvider.GetRequiredService<PrinterCommandService>();

        return await service.SendCommandAsync(printerId, command, userId, CancellationToken.None);
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

        return await wsClient.ConnectAsync(request.Uri, cancellationToken);
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
