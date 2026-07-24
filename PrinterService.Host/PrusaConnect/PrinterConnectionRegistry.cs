using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Devlooped.Net;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Write-only view of a printer's live connection. Command-sending code has no business touching
/// <c>Input</c>, <c>State</c> transitions, or <c>CompleteAsync</c> - only writing a frame.
/// </summary>
public interface IPrinterConnection
{
    bool IsOpen { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}

public sealed class WebSocketPrinterConnection : IPrinterConnection
{
    private readonly IWebSocketPipe _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WebSocketPrinterConnection(IWebSocketPipe pipe)
    {
        _pipe = pipe;
    }

    public bool IsOpen => _pipe.State == WebSocketState.Open;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            await _pipe.Output.WriteAsync(frame, cancellationToken);
            await _pipe.Output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>
/// Maps a connected printer's id to its live connection, so a command can be sent from outside the
/// request that accepted the WebSocket upgrade. Registered/unregistered by
/// <see cref="Controllers.PrusaConnectPrinterController.ConnectWebSocket"/> for the lifetime of that
/// request.
/// </summary>
public sealed class PrinterConnectionRegistry
{
    private readonly ConcurrentDictionary<int, IPrinterConnection> _connections = new();

    public void Register(int printerId, IPrinterConnection connection)
    {
        _connections[printerId] = connection;
    }

    /// <summary>
    /// Conditional (instance-matching) remove. A fast reconnect registers a new connection for the
    /// same <paramref name="printerId"/> before the old request's <c>finally</c> unregisters the old
    /// one; an unconditional remove would delete the new, live connection instead of the stale one.
    /// </summary>
    public void Unregister(int printerId, IPrinterConnection connection)
    {
        _connections.TryRemove(new KeyValuePair<int, IPrinterConnection>(printerId, connection));
    }

    public bool TryGet(int printerId, out IPrinterConnection? connection) => _connections.TryGetValue(printerId, out connection);

    public bool IsConnected(int printerId) => _connections.TryGetValue(printerId, out IPrinterConnection? connection) && connection.IsOpen;
}
