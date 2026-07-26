using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using PrinterService.Data;
using PrinterService.Host.PrusaConnect;
using PrinterService.Host.Services;
using PrinterService.Model.Entities;

namespace PrinterService.Host.E2ETest;

/// <summary>
/// Drives a real WebSocket handshake against <c>/p/ws</c> through the actual ASP.NET Core pipeline -
/// routing, <c>PrusaConnectPrinterAuthenticationHandler</c>, and <c>[Authorize]</c> - rather than
/// calling <see cref="WebSocketHandler"/> directly the way <c>WebSocketHandlerParsingTests</c> does.
/// </summary>
/// <remarks>
/// Written after re-enabling <c>[Authorize]</c> on <c>PrusaConnectPrinterController</c> (commented out
/// since December, before the Fingerprint-based auth handler existed). Every other test in this
/// project that exercises the handler bypasses authentication entirely, so nothing had ever proven
/// the gate itself rejects a bad handshake or that a good one actually resolves the claimed printer's
/// id and threads it through to the handler.
/// </remarks>
[Collection("WebApplicationFactory")]
public sealed class PrusaConnectWebSocketTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-e2e-ws-{Guid.NewGuid():N}.db");
    private readonly CapturingMessageDispatcher _dispatcher = new();
    private readonly CapturingSink _logs = new();
    private PrinterServiceFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new PrinterServiceFactory($"Data Source={_databasePath}", _dispatcher, _logs);

        // Force the host to actually start (migrations + AdminBootstrap run at that point) before any
        // test touches it, rather than lazily on the first HttpClient call.
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

        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// A valid Fingerprint/Token pair, only obtainable by actually registering and claiming a printer
    /// through the real HTTP flow - the same one <c>EndToEndEnrollmentTests</c> drives - rather than
    /// hand-hashing a token straight into the database. The point of this suite is to prove the real
    /// handler chain accepts what it should, so the credentials it uses have to come from that chain.
    /// </summary>
    private async Task<(string fingerprint, string token, int printerId)> EnrollAndClaimPrinterAsync(string fingerprint)
    {
        using HttpClient anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage registerResponse = await EnrollmentFlowHelper.SendPrinterRegisterAsync(anonymous, new
        {
            sn = $"SN-{fingerprint}",
            fingerprint,
            printer_type = "1.3.5",
            firmware = "6.4.0+11974",
        });
        registerResponse.EnsureSuccessStatusCode();
        string code = registerResponse.Headers.GetValues("Code").Single();

        (PSUser _, HttpClient appClient) = await EnrollmentFlowHelper.CreateAuthenticatedUserAsync(_factory, $"{fingerprint}@example.com");
        using (appClient)
        {
            HttpResponseMessage claimResponse = await appClient.PostAsJsonAsync("/api/v1/printers/register", new
            {
                name = "WS test printer",
                location = "Bench",
                code,
            });

            claimResponse.EnsureSuccessStatusCode();
        }

        HttpResponseMessage pollResponse = await EnrollmentFlowHelper.SendPollAsync(anonymous, code);
        pollResponse.EnsureSuccessStatusCode();
        string token = pollResponse.Headers.GetValues("Token").Single();

        using IServiceScope scope = _factory.Services.CreateScope();
        PSDbContext context = scope.ServiceProvider.GetRequiredService<PSDbContext>();

        PrusaConnectAuthenticationData auth = await context.PrusaConnectAuthentication
            .Include(a => a.Printer)
            .SingleAsync(a => a.FingerPrintKey == PrinterFingerprint.Key(fingerprint));

        // The caller gets the truncated form back, because that is what a real printer would present
        // on the upgrade: /p/register's body carries the whole fingerprint, every header carries only
        // its first 16 characters (see PrinterFingerprint).
        return (PrinterFingerprint.Key(fingerprint), token, auth.Printer!.Id);
    }

    /// <summary>
    /// A genuinely claimed printer's Fingerprint/Token upgrades the connection, and the printer id
    /// resolved from that Fingerprint at the auth handler - not just any id - is what reaches
    /// <c>WebSocketHandler.JsonMessageReceived</c>.
    /// </summary>
    [Fact]
    public async Task ValidCredentialsUpgradeTheConnectionAndReachTheHandlerWithTheClaimedPrinterId()
    {
        // Arrange
        (string fingerprint, string token, int printerId) = await EnrollAndClaimPrinterAsync("WS-FINGERPRINT-VALID");

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.SubProtocols.Add(Headers.Values.WSProtocolPrusaConnect);
        wsClient.ConfigureRequest = request =>
        {
            request.Headers[Headers.Fingerprint] = fingerprint;
            request.Headers[Headers.Token] = token;
            request.Headers[Headers.UserAgentPrinter] = "Buddy";
            request.Headers[Headers.UserAgentVersion] = "6.4.0";
        };

        // Act
        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://localhost/p/ws"), CancellationToken.None);

        socket.State.Should().Be(WebSocketState.Open);

        byte[] message = Encoding.UTF8.GetBytes("""{"state":"IDLE"}""");
        await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        // The server-side read loop runs on its own task; give it a moment to observe the message
        // before tearing the connection down.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);

        // Assert
        _dispatcher.Calls.Should().ContainSingle()
            .Which.printerId.Should().Be(printerId,
                "the id resolved from the Fingerprint header at upgrade must be the one threaded to the handler");

        _dispatcher.Calls[0].root.GetProperty("state").GetString().Should().Be("IDLE",
                "the dispatcher must receive the message the printer actually sent, not just any message");
    }

    /// <summary>
    /// Opens an authenticated socket the way a printer does, so a test can drive the close paths.
    /// </summary>
    private async Task<WebSocket> ConnectAsPrinterAsync(string fingerprintSeed)
    {
        (string fingerprint, string token, int _) = await EnrollAndClaimPrinterAsync(fingerprintSeed);

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.SubProtocols.Add(Headers.Values.WSProtocolPrusaConnect);
        wsClient.ConfigureRequest = request =>
        {
            request.Headers[Headers.Fingerprint] = fingerprint;
            request.Headers[Headers.Token] = token;
            request.Headers[Headers.UserAgentPrinter] = "Buddy";
            request.Headers[Headers.UserAgentVersion] = "6.4.0";
        };

        return await wsClient.ConnectAsync(new Uri("ws://localhost/p/ws"), CancellationToken.None);
    }

    /// <summary>
    /// The printer closing its end gets a <c>NormalClosure</c> back, and the request finishes without
    /// anything failing server-side.
    /// </summary>
    /// <remarks>
    /// Closing moved from <c>WebSocketHandler</c> to the controller when WebSocketPipe was replaced,
    /// and nothing covered the result. The log assertion is the load-bearing half: once the socket
    /// has upgraded the response has already started, so an exception thrown after the action
    /// returns - during result execution, outside the controller's own try - reaches neither the
    /// client nor any assertion on the socket. It is only visible in the log.
    /// </remarks>
    [Fact]
    public async Task CleanDisconnectClosesWithNormalClosureAndFailsNothingServerSide()
    {
        // Arrange
        using WebSocket socket = await ConnectAsPrinterAsync("WS-FINGERPRINT-CLEAN-CLOSE");

        // Act
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure,
            "the server answers a clean disconnect with a normal close");

        // Result execution runs after the action returns, so give any failure a chance to surface
        // rather than asserting into a gap where it simply has not happened yet.
        await WaitForFailuresOrTimeoutAsync();

        _logs.Failures.Should().BeEmpty("a clean disconnect is not an error path");
    }

    /// <summary>
    /// Malformed JSON is a protocol violation: the socket is closed with <c>PolicyViolation</c>, the
    /// status the printer sees, rather than the connection merely dropping.
    /// </summary>
    [Fact]
    public async Task MalformedJsonClosesWithPolicyViolation()
    {
        // Arrange
        using WebSocket socket = await ConnectAsPrinterAsync("WS-FINGERPRINT-BAD-JSON");

        // Act
        byte[] garbage = Encoding.UTF8.GetBytes("""{"job_id":301,,,}""");
        await socket.SendAsync(garbage, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        byte[] buffer = new byte[256];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None)
                                                    .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        result.MessageType.Should().Be(WebSocketMessageType.Close,
            "garbage on the wire ends the connection rather than being skipped");
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    /// <summary>
    /// Waits briefly for a server-side failure to appear, so an assertion that none occurred is
    /// testing the server rather than racing it.
    /// </summary>
    private async Task WaitForFailuresOrTimeoutAsync()
    {
        for (int i = 0; i < 40 && _logs.Failures.Count == 0; i++)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// A fingerprint nothing has ever registered is rejected before the socket upgrades - the gate
    /// this whole change re-enables, proven at the HTTP level rather than only at the handler's own
    /// unit tests.
    /// </summary>
    [Fact]
    public async Task UnknownFingerprintIsRejectedWithoutUpgrading()
    {
        // Arrange
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request =>
        {
            request.Headers[Headers.Fingerprint] = "no-such-fingerprint";
            request.Headers[Headers.Token] = "irrelevant-token";
            request.Headers[Headers.UserAgentPrinter] = "Buddy";
            request.Headers[Headers.UserAgentVersion] = "6.4.0";
        };

        // Act
        Func<Task> act = async () => await wsClient.ConnectAsync(new Uri("ws://localhost/p/ws"), CancellationToken.None);

        // Assert
        // TestServer's WebSocketClient throws rather than returning a closed/faulted socket when the
        // server never reaches 101 Switching Protocols.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*401*");
    }

    /// <summary>
    /// No Fingerprint/Token/User-Agent headers at all - the shape of a client that isn't Buddy - is
    /// rejected the same way as a known-bad fingerprint, not treated as anonymous-but-allowed.
    /// </summary>
    [Fact]
    public async Task MissingHeadersAreRejectedWithoutUpgrading()
    {
        // Arrange
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();

        // Act
        Func<Task> act = async () => await wsClient.ConnectAsync(new Uri("ws://localhost/p/ws"), CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*401*");
    }
}
