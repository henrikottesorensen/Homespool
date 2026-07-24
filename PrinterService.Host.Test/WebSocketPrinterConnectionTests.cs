using System.Buffers;
using System.Text;
using System.Threading;

using AwesomeAssertions;

using PrinterService.Host.PrusaConnect;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="WebSocketPrinterConnection"/> - confirms bytes handed to <c>SendAsync</c> reach the
/// underlying pipe's <c>Output</c> unchanged, using <see cref="FakeWebSocketPipe"/> in reverse: what
/// the server writes to <c>Output</c> is what "the printer" would read from <c>Input</c>.
/// </summary>
public class WebSocketPrinterConnectionTests
{
    [Fact]
    public async System.Threading.Tasks.Task SendAsyncWritesTheExactFrameBytesToThePipe()
    {
        // Arrange
        using FakeWebSocketPipe pipe = new();
        WebSocketPrinterConnection connection = new(pipe);
        byte[] frame = Encoding.ASCII.GetBytes("J0000002A{\"command\":\"PAUSE_PRINT\"}");

        // Act
        await connection.SendAsync(frame, CancellationToken.None);
        await pipe.FinishAsync();

        System.IO.Pipelines.ReadResult result = await pipe.Input.ReadAsync();

        // Assert
        result.Buffer.ToArray().Should().Equal(frame);
    }

    [Fact]
    public void IsOpenReflectsThePipesState()
    {
        // Arrange
        using FakeWebSocketPipe pipe = new();
        WebSocketPrinterConnection connection = new(pipe);

        // Act + Assert
        connection.IsOpen.Should().BeTrue();

        pipe.Close();
        connection.IsOpen.Should().BeFalse();
    }
}
