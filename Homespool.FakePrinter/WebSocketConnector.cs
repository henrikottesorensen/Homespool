using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.FakePrinter;

/// <summary>
/// Performs the WebSocket upgrade for <see cref="FakePrinterClient.ConnectAsync"/>. The default
/// (a real <see cref="ClientWebSocket"/>) reaches any running server; in-process tests substitute
/// one built on <c>TestServer.CreateWebSocketClient()</c>, which is how the same fake drives both
/// the CLI-against-Kestrel and the xUnit-against-TestServer modes.
/// </summary>
public delegate Task<WebSocket> WebSocketConnector(FakePrinterConnectRequest request, CancellationToken cancellationToken);
