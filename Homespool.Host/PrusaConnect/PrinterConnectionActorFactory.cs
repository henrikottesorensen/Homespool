using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Homespool.Host.PrusaConnect.Transfers;
using Homespool.Host.Telemetry;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Creates a <see cref="PrinterConnectionActor"/> for each accepted WebSocket, so
/// <see cref="Controllers.PrusaConnectPrinterController"/> doesn't have to carry the actor's
/// singleton dependencies (sink, options, logging) itself.
/// </summary>
/// <remarks>
/// Unsealed, with a <c>virtual</c> <see cref="Create"/>, so a test can hand
/// <see cref="PrinterConnectionSession"/> an actor it controls - one whose completion never
/// finishes, or faults - without a socket to build a real one over.
/// </remarks>
public class PrinterConnectionActorFactory
{
    private readonly ITelemetrySink _sink;
    private readonly ITransferContentStore _contentStore;
    private readonly ILogger<PrinterConnectionActor> _logger;
    private readonly IOptionsMonitor<PrusaConnectOptions> _options;
    private readonly PrinterTrafficLog _trafficLog;

    public PrinterConnectionActorFactory(ITelemetrySink sink,
                                         ILogger<PrinterConnectionActor> logger,
                                         IOptionsMonitor<PrusaConnectOptions> options,
                                         ITransferContentStore contentStore,
                                         PrinterTrafficLog trafficLog)
    {
        _sink = sink;
        _contentStore = contentStore;
        _logger = logger;
        _options = options;
        _trafficLog = trafficLog;
    }

    /// <summary>Creates the actor and starts its loop; the caller owns completion via
    /// <see cref="Printing.IPrinterLink.Complete"/>.</summary>
    public virtual IPrinterConnectionActor Create(int printerId, IPrinterConnection connection)
    {
        return new PrinterConnectionActor(printerId, connection, _sink, _logger, _options.CurrentValue.CommandResponseTimeout,
                                          _contentStore)
        {
            TrafficLog = _trafficLog,
        };
    }
}
