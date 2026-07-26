using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterService.Host.Test;

/// <summary>
/// A minimal in-memory <see cref="WebSocket"/> covering the write side: records every frame handed
/// to <see cref="SendAsync"/> - bytes, message type and end-of-message flag, all three of which are
/// wire contract - and lets a test flip <see cref="State"/>, which is everything
/// <see cref="PrusaConnect.WebSocketPrinterConnection"/> touches. Receiving deliberately throws:
/// the read side never goes through this class - the parsing tests feed <c>WebSocketHandler</c> a
/// <see cref="System.IO.Pipelines.PipeReader"/> directly, no socket involved.
/// </summary>
/// <remarks>
/// <see cref="Operations"/> and <see cref="HoldSends"/> exist so a test can pin <i>ordering</i>
/// rather than just content. A close frame is a send like any other as far as the wire is concerned,
/// so "did a close slip out while a data frame was still going" is a question only a chronological
/// record can answer.
/// </remarks>
internal sealed class FakeWebSocket : WebSocket
{
    /// <summary>Recorded in <see cref="Operations"/> when a send enters the socket.</summary>
    public const string SendStarted = "send-start";

    /// <summary>Recorded when that send finishes writing its frame.</summary>
    public const string SendCompleted = "send-end";

    /// <summary>Recorded when a close frame is written.</summary>
    public const string CloseFrameSent = "close";

    private readonly object _sync = new();
    private readonly List<string> _operations = [];
    private readonly TaskCompletionSource _sendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _holdSends;
    private WebSocketState _state = WebSocketState.Open;

    public List<(byte[] frame, WebSocketMessageType messageType, bool endOfMessage)> Sent { get; } = [];

    /// <summary>Chronological record of send and close activity on this socket.</summary>
    public IReadOnlyList<string> Operations
    {
        get
        {
            lock (_sync)
            {
                return _operations.ToList();
            }
        }
    }

    public override WebSocketCloseStatus? CloseStatus { get; }

    public override string? CloseStatusDescription { get; }

    public override WebSocketState State => _state;

    public override string? SubProtocol { get; }

    /// <summary>
    /// Parks every subsequent <see cref="SendAsync"/> after it has recorded
    /// <see cref="SendStarted"/>, until <see cref="ReleaseSends"/>. Simulates a send that is
    /// genuinely mid-flight - the state a concurrent close has to respect.
    /// </summary>
    public void HoldSends() => _holdSends = true;

    public void ReleaseSends() => _sendGate.TrySetResult();

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;

        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Record(CloseFrameSent);
        _state = WebSocketState.CloseSent;

        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake only supports the write side; parse-loop tests read from a PipeReader instead.");

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        Record(SendStarted);

        if (_holdSends)
        {
            await _sendGate.Task;
        }

        lock (_sync)
        {
            // Copied: the segment wraps a buffer the caller is free to reuse after the send completes.
            Sent.Add((buffer.ToArray(), messageType, endOfMessage));
            _operations.Add(SendCompleted);
        }
    }

    /// <summary>Closes the socket, as the remote peer going away would.</summary>
    public void Close() => _state = WebSocketState.Closed;

    private void Record(string operation)
    {
        lock (_sync)
        {
            _operations.Add(operation);
        }
    }
}
