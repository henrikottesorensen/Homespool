using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterService.Host.Test;

/// <summary>
/// A minimal in-memory <see cref="WebSocket"/> covering the write side: records every frame handed
/// to <see cref="SendAsync"/> - bytes, message type and end-of-message flag, all three of which are
/// wire contract - and lets a test flip <see cref="State"/>, which is everything
/// <see cref="PrusaConnect.WebSocketPrinterConnection"/> touches. Receiving deliberately throws:
/// the read side never goes through this class - the parsing tests feed
/// <c>WebSocketHandler</c> a <see cref="System.IO.Pipelines.PipeReader"/> directly, no socket
/// involved.
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private WebSocketState _state = WebSocketState.Open;

    public List<(byte[] Frame, WebSocketMessageType MessageType, bool EndOfMessage)> Sent { get; } = [];

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;

        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;

        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake only supports the write side; parse-loop tests read from a PipeReader instead.");

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        // Copied: the segment wraps a buffer the caller is free to reuse after the send completes.
        Sent.Add((buffer.ToArray(), messageType, endOfMessage));

        return Task.CompletedTask;
    }

    /// <summary>Closes the socket, as the remote peer going away would.</summary>
    public void Close() => _state = WebSocketState.Closed;
}
