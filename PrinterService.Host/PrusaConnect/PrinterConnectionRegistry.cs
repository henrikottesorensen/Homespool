using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Write-only view of a printer's live connection. Command-sending code has no business touching
/// the receive side, <c>State</c> transitions, or the close handshake - only writing a frame.
/// </summary>
public interface IPrinterConnection
{
    bool IsOpen { get; }

    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}

public sealed class WebSocketPrinterConnection : IPrinterConnection
{
    private readonly WebSocket _webSocket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WebSocketPrinterConnection(WebSocket webSocket)
    {
        _webSocket = webSocket;
    }

    public bool IsOpen => _webSocket.State == WebSocketState.Open;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        // The lock is load-bearing: WebSocket.SendAsync forbids concurrent sends outright, and two
        // requests can command the same printer at once.
        await _writeLock.WaitAsync(cancellationToken);

        try
        {
            // Binary, endOfMessage per frame: the exact wire shape WebSocketPipe produced, which is
            // what the firmware has been accepting all along.
            await _webSocket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
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
