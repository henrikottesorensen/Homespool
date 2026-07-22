using System.IO.Pipelines;
using System.Net.WebSockets;

using Devlooped.Net;

namespace PrinterService.Api.Test;

/// <summary>
/// An <see cref="IWebSocketPipe"/> backed by an in-memory <see cref="Pipe"/>, so a test can
/// deliver bytes to <c>WebSocketHandler</c> in precisely controlled chunks.
/// </summary>
/// <remarks>
/// The chunking is the whole point: a real WebSocket frame boundary falls wherever the network
/// puts it, with no regard for JSON document boundaries, and that is exactly the case the handler
/// has to survive. Feeding a message a byte at a time is the harshest honest simulation of it.
/// </remarks>
public sealed class FakeWebSocketPipe : IWebSocketPipe
{
    private readonly Pipe _pipe = new();

    private volatile bool _closed;

    public PipeReader Input => _pipe.Reader;

    public PipeWriter Output => _pipe.Writer;

    /// <summary>
    /// The handler loops while this is <see cref="WebSocketState.Open"/>, so flipping it is how a
    /// test ends the run.
    /// </summary>
    public WebSocketState State => _closed ? WebSocketState.Closed : WebSocketState.Open;

    public WebSocketCloseStatus? CloseStatus { get; private set; }

    public string CloseStatusDescription { get; private set; } = string.Empty;

    public string SubProtocol => "prusa-connect";

    /// <summary>True once the handler asked to close, whatever the reason.</summary>
    public bool CompleteAsyncCalled { get; private set; }

    public Task CompleteAsync(WebSocketCloseStatus? closeStatus = null, string? statusDescription = null)
    {
        CompleteAsyncCalled = true;
        CloseStatus = closeStatus;
        CloseStatusDescription = statusDescription ?? string.Empty;
        _closed = true;

        return Task.CompletedTask;
    }

    public Task RunAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Writes <paramref name="payload"/> to the handler in <paramref name="chunkSize"/>-byte
    /// pieces, flushing each one so it becomes a separate read — the equivalent of arriving in
    /// separate frames.
    /// </summary>
    public async Task WriteInChunksAsync(byte[] payload, int chunkSize)
    {
        for (int offset = 0; offset < payload.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, payload.Length - offset);

            await _pipe.Writer.WriteAsync(payload.AsMemory(offset, length));
            await _pipe.Writer.FlushAsync();
        }
    }

    /// <summary>
    /// Signals end of input. The socket deliberately stays <see cref="WebSocketState.Open"/>: the
    /// handler is expected to drain what is already buffered and stop when the read reports
    /// completion. Closing here instead would let the handler bail out with data still pending,
    /// which makes tests race against their own teardown.
    /// </summary>
    public Task FinishAsync() => _pipe.Writer.CompleteAsync().AsTask();

    /// <summary>Closes the socket, as the remote peer going away would.</summary>
    public void Close() => _closed = true;

    public void Dispose()
    {
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();
    }
}
