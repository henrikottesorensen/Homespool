using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PrinterService.Host.PrusaConnect.Commands;

namespace PrinterService.Host.PrusaConnect;

public enum CommandSendOutcome
{
    Completed,
    NotConnected,
    AlreadyInFlight,
    TimedOut,
}

public sealed record CommandSendResult(CommandSendOutcome Outcome, CommandOutcome? Response);

/// <summary>
/// Sends a command to a connected printer and awaits its reply, correlated via
/// <see cref="IPrinterCommandCorrelator"/>. Transport-only: no permission checks and no database
/// access - see <see cref="PrinterCommandService"/> for that layer.
/// </summary>
public interface IPrinterCommandTransport
{
    Task<CommandSendResult> SendAsync(int printerId, ISendableCommand commandData, CancellationToken cancellationToken);
}

public sealed class PrinterCommandTransport : IPrinterCommandTransport
{
    private readonly PrinterConnectionRegistry _registry;
    private readonly IPrinterCommandCorrelator _correlator;
    private readonly ILogger<PrinterCommandTransport> _logger;
    private readonly TimeSpan _responseTimeout;
    private int _lastCommandId;

    public PrinterCommandTransport(PrinterConnectionRegistry registry, IPrinterCommandCorrelator correlator,
        ILogger<PrinterCommandTransport> logger, IOptions<PrusaConnectOptions> options)
        : this(registry, correlator, logger, options.Value.CommandResponseTimeout)
    {
    }

    /// <summary>Overload with an explicit response timeout, mainly so tests aren't stuck waiting out
    /// <see cref="PrusaConnectOptions.CommandResponseTimeoutSeconds"/>'s configured default.</summary>
    public PrinterCommandTransport(PrinterConnectionRegistry registry, IPrinterCommandCorrelator correlator,
        ILogger<PrinterCommandTransport> logger, TimeSpan responseTimeout)
    {
        _registry = registry;
        _correlator = correlator;
        _logger = logger;
        _responseTimeout = responseTimeout;
    }

    public async Task<CommandSendResult> SendAsync(int printerId, ISendableCommand commandData, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(printerId, out IPrinterConnection? connection) || connection is null || !connection.IsOpen)
        {
            return new CommandSendResult(CommandSendOutcome.NotConnected, null);
        }

        // A per-process monotonic counter is fine here: the firmware allows only one in-flight
        // command per printer at a time anyway (connect.cpp:469-476), and a collision would need a
        // full 2^32-command cycle within that single in-flight window.
        uint commandId = unchecked((uint)Interlocked.Increment(ref _lastCommandId));

        if (!_correlator.TryBeginCommand(printerId, commandId, out Task<CommandOutcome> outcomeTask))
        {
            return new CommandSendResult(CommandSendOutcome.AlreadyInFlight, null);
        }

        try
        {
            byte[] frame = CommandWireEncoder.Encode(commandId, commandData);
            await connection.SendAsync(frame, cancellationToken);
        }
        catch
        {
            // Never reached the printer - don't leave it wedged as pending forever.
            _correlator.Cancel(printerId);
            throw;
        }

        using CancellationTokenSource timeoutCts = new(_responseTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            CommandOutcome outcome = await outcomeTask.WaitAsync(linked.Token);

            return new CommandSendResult(CommandSendOutcome.Completed, outcome);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _correlator.Cancel(printerId);
            _logger.LogWarning("[{PrinterId}] command {CommandId} ({Command}) timed out waiting for a reply",
                printerId, commandId, commandData.WireName);

            return new CommandSendResult(CommandSendOutcome.TimedOut, null);
        }
    }
}
