using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

using AwesomeAssertions;

using PrinterService.Host.PrusaConnect;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="WebSocketPrinterConnection"/> - confirms a frame handed to <c>SendAsync</c> reaches
/// the socket unchanged, and as a single Binary message. The message shape is asserted, not just
/// the bytes: Binary-with-endOfMessage is what WebSocketPipe sent before the swap to the raw
/// socket, i.e. the framing the firmware has been accepting all along.
/// </summary>
public class WebSocketPrinterConnectionTests
{
    [Fact]
    public async System.Threading.Tasks.Task SendAsyncSendsTheExactFrameBytesAsOneBinaryMessage()
    {
        // Arrange
        using FakeWebSocket socket = new();
        WebSocketPrinterConnection connection = new(socket);
        byte[] frame = Encoding.ASCII.GetBytes("J0000002A{\"command\":\"PAUSE_PRINT\"}");

        // Act
        await connection.SendAsync(frame, CancellationToken.None);

        // Assert
        socket.Sent.Should().ContainSingle();
        socket.Sent[0].frame.Should().Equal(frame);
        socket.Sent[0].messageType.Should().Be(WebSocketMessageType.Binary);
        socket.Sent[0].endOfMessage.Should().BeTrue();
    }

    /// <summary>
    /// The close frame must not overtake a command send that is still on the wire.
    /// </summary>
    /// <remarks>
    /// A close is a send as far as the socket is concerned, so the write lock has to cover it too.
    /// It did not: the controller called <c>webSocket.CloseOutputAsync</c> straight on the socket,
    /// bypassing <c>WebSocketPrinterConnection</c> entirely, so teardown could interleave with a
    /// command a request thread was still writing - putting a data frame after a close frame, or
    /// losing the race outright and failing the API call the command came from.
    /// </remarks>
    [Fact]
    public async System.Threading.Tasks.Task CloseDoesNotInterleaveWithAnInFlightSend()
    {
        // Arrange
        using FakeWebSocket socket = new();
        WebSocketPrinterConnection connection = new(socket);
        socket.HoldSends();

        // A command send is in flight: inside the socket, holding the connection's write lock.
        System.Threading.Tasks.Task send =
            connection.SendAsync(Encoding.ASCII.GetBytes("J0000002A{\"command\":\"PAUSE_PRINT\"}"), CancellationToken.None).AsTask();

        await WaitUntilAsync(() => socket.Operations.Contains(FakeWebSocket.SendStarted));

        // Act - teardown closes while that send is still outstanding.
        System.Threading.Tasks.Task close = connection.CloseOutputAsync(WebSocketCloseStatus.NormalClosure);

        // Give an unserialized close time to slip out ahead of the send it should be waiting for;
        // without this the assertion could pass on timing rather than on ordering.
        await System.Threading.Tasks.Task.Delay(50);

        socket.ReleaseSends();
        await System.Threading.Tasks.Task.WhenAll(send, close).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        socket.Operations.Should().Equal(
            [FakeWebSocket.SendStarted, FakeWebSocket.SendCompleted, FakeWebSocket.CloseFrameSent],
            "the close frame must wait for the in-flight send to finish, not interleave with it");
    }

    private static async System.Threading.Tasks.Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await System.Threading.Tasks.Task.Delay(10);
        }

        condition().Should().BeTrue("the awaited condition should have been reached by now");
    }

    [Fact]
    public void IsOpenReflectsTheSocketsState()
    {
        // Arrange
        using FakeWebSocket socket = new();
        WebSocketPrinterConnection connection = new(socket);

        // Act + Assert
        connection.IsOpen.Should().BeTrue();

        socket.Close();
        connection.IsOpen.Should().BeFalse();
    }
}
