using System.Net.WebSockets;
using System.Threading.Tasks;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// A connection whose close handshake its owner can drive - i.e. the accepting request, and nothing
/// else. Kept separate from <see cref="IPrinterConnection"/> rather than folded into it so the
/// narrowing that interface documents survives: command-sending code is handed the base interface
/// and still cannot reach the close, while <see cref="PrinterConnectionSession"/> gets the seam its
/// teardown needs.
/// </summary>
public interface IClosablePrinterConnection : IPrinterConnection
{
    Task CloseOutputAsync(WebSocketCloseStatus closeStatus);
}
