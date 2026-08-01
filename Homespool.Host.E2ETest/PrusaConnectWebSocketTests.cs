using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Homespool.FakePrinter;
using Homespool.Host.PrusaConnect;
using Homespool.Host.Services;

namespace Homespool.Host.E2ETest;

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
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}", _dispatcher, _logs);

        // Force the host to actually start (migrations + AdminBootstrap run at that point) before any
        // test touches it, rather than lazily on the first HttpClient call.
        _ = _factory.Server;

        using IServiceScope scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SetupState>().MarkComplete();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();

        return ValueTask.CompletedTask;
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
    /// A genuinely claimed printer's Fingerprint/Token upgrades the connection, and the printer id
    /// resolved from that Fingerprint at the auth handler - not just any id - is what reaches
    /// <c>WebSocketHandler.JsonMessageReceived</c>.
    /// </summary>
    [Fact]
    public async Task ValidCredentialsUpgradeTheConnectionAndReachTheHandlerWithTheClaimedPrinterId()
    {
        // Arrange
        // Enrolment via the shared helper; the upgrade presents the identity's 16-character
        // header form, the same truncation a real printer sends (see PrinterFingerprint).
        (PrinterIdentity identity, string token, int printerId, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.SubProtocols.Add(Headers.Values.WSProtocolPrusaConnect);
        wsClient.ConfigureRequest = request =>
        {
            request.Headers[Headers.Fingerprint] = identity.HeaderFingerprint;
            request.Headers[Headers.Token] = token;
            request.Headers[Headers.UserAgentPrinter] = "Buddy";
            request.Headers[Headers.UserAgentVersion] = "6.4.0";
        };

        // Act
        using WebSocket socket = await wsClient.ConnectAsync(PrinterListener.WebSocketUri(_factory), CancellationToken.None);

        socket.State.Should().Be(WebSocketState.Open);

        byte[] message = Encoding.UTF8.GetBytes("""{"state":"IDLE"}""");
        await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        // The server-side read loop runs on its own task; give it a moment to observe the message
        // before tearing the connection down.
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

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
    private async Task<WebSocket> ConnectAsPrinterAsync()
    {
        (PrinterIdentity identity, string token, int _, long _) =
            await EnrolmentFlowHelper.EnrolAndClaimFakePrinterAsync(_factory);

        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.SubProtocols.Add(Headers.Values.WSProtocolPrusaConnect);
        wsClient.ConfigureRequest = request =>
        {
            request.Headers[Headers.Fingerprint] = identity.HeaderFingerprint;
            request.Headers[Headers.Token] = token;
            request.Headers[Headers.UserAgentPrinter] = "Buddy";
            request.Headers[Headers.UserAgentVersion] = "6.4.0";
        };

        return await wsClient.ConnectAsync(PrinterListener.WebSocketUri(_factory), CancellationToken.None);
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
        using WebSocket socket = await ConnectAsPrinterAsync();

        // Act
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
        using WebSocket socket = await ConnectAsPrinterAsync();

        // Act
        byte[] garbage = Encoding.UTF8.GetBytes("""{"job_id":301,,,}""");
        await socket.SendAsync(garbage, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        byte[] buffer = new byte[256];
        WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken)
                                                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
        Func<Task> act = async () => await wsClient.ConnectAsync(PrinterListener.WebSocketUri(_factory), CancellationToken.None);

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
        Func<Task> act = async () => await wsClient.ConnectAsync(PrinterListener.WebSocketUri(_factory), CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*401*");
    }
}
