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
        socket.Sent[0].Frame.Should().Equal(frame);
        socket.Sent[0].MessageType.Should().Be(WebSocketMessageType.Binary);
        socket.Sent[0].EndOfMessage.Should().BeTrue();
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
