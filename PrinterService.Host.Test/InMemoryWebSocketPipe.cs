using System;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Devlooped.Net;

namespace PrinterService.Host.Test;

/// <summary>
/// A real, working <see cref="IWebSocketPipe"/> backed by an in-memory <see cref="Pipe"/> - a test
/// dictates exactly where the frame boundaries fall on the way in, and can read back whatever the
/// server wrote on the way out.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a substitute, and not substitutable: the chunking is the whole point, and a
/// mocking framework cannot fragment a byte stream. A real WebSocket frame boundary falls wherever
/// the network puts it, with no regard for JSON document boundaries, and that is exactly the case
/// <c>WebSocketHandler</c> has to survive - <c>WriteInChunksAsync</c> can feed it a message a byte
/// at a time, splitting even a multi-byte UTF-8 character down the middle, which is the harshest
/// honest simulation of it.
/// </para>
/// <para>
/// The single underlying <see cref="Pipe"/> is also what lets it be used in reverse: bytes the
/// server writes to <see cref="Output"/> are readable from <see cref="Input"/>, so a test can play
/// "the printer" and assert on exactly what reached the wire.
/// </para>
/// </remarks>
public sealed class InMemoryWebSocketPipe : IWebSocketPipe
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

    public Task CompleteAsync(WebSocketCloseStatus? closeStatus = null, string? closeStatusDescription = null)
    {
        CompleteAsyncCalled = true;
        CloseStatus = closeStatus;
        CloseStatusDescription = closeStatusDescription ?? string.Empty;
        _closed = true;

        return Task.CompletedTask;
    }

    public Task RunAsync(CancellationToken cancellation = default) => Task.CompletedTask;

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
