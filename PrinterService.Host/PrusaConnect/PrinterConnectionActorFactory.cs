using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PrinterService.Host.PrusaConnect;

/// <summary>
/// Creates a <see cref="PrinterConnectionActor"/> for each accepted WebSocket, so
/// <see cref="Controllers.PrusaConnectPrinterController"/> doesn't have to carry the actor's
/// singleton dependencies (sink, options, logging) itself.
/// </summary>
public sealed class PrinterConnectionActorFactory
{
    private readonly ITelemetrySink _sink;
    private readonly ILogger<PrinterConnectionActor> _logger;
    private readonly IOptions<PrusaConnectOptions> _options;

    public PrinterConnectionActorFactory(ITelemetrySink sink, ILogger<PrinterConnectionActor> logger, IOptions<PrusaConnectOptions> options)
    {
        _sink = sink;
        _logger = logger;
        _options = options;
    }

    /// <summary>Creates the actor and starts its loop; the caller owns completion via
    /// <see cref="IPrinterConnectionActor.Complete"/>.</summary>
    public IPrinterConnectionActor Create(int printerId, IPrinterConnection connection) =>
        new PrinterConnectionActor(printerId, connection, _sink, _logger, _options.Value.CommandResponseTimeout);
}
