using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Homespool.Host.Printing;
using Homespool.Host.Queue;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Owns the lifetime of every printer connected over the pre-websocket HTTP transport - the piece
/// a socket gets for free from the request that holds it, and this transport has to build.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no request to hang a lifetime on.</b> A WebSocket printer's actor lives exactly as
/// long as the upgrade request, and <see cref="PrinterConnectionSession"/> creates, registers, runs
/// and tears it down inside that request. An HTTP printer is a stream of independent POSTs, so
/// something else must decide when it arrived (its first authenticated POST) and when it left
/// (nothing heard for <see cref="HttpPrinterConnection.IdleWindow"/>). That something is this class:
/// <see cref="GetOrCreate"/> on the way in, and the reaper on the way out.
/// </para>
/// <para>
/// <b>Registered once, not per POST.</b> <see cref="PrinterConnectionRegistry.Register"/> displaces
/// whatever it replaces and logs it as a possible credential compromise, which is right for a second
/// socket and would be a spurious error on every telemetry message here. So a printer's actor is
/// registered when its session is created and never again, and a printer that really is presenting
/// on both transports at once still hits that displacement path - honestly, because it is the same
/// situation.
/// </para>
/// <para>
/// <b>Get-or-create and reap agree under one lock</b>, and that lock is the whole reason this is a
/// class rather than a <c>ConcurrentDictionary</c>: the race worth closing is a POST arriving as
/// the reaper decides the printer has gone. Under the lock, either the POST touches the session
/// first and the reaper sees it as live, or the reaper removes it first and the POST creates a
/// fresh one. Neither hands the printer's message to an actor that is being torn down.
/// </para>
/// <para>
/// <b>Reaping reuses the socket session's teardown ordering</b> - unregister, complete, bounded
/// drain - because the invariants it was bought with hold here too: unregister first so no command
/// can be parked on a session that is about to fail everything, complete so the loop's own drain
/// reports the parked command as <c>NotConnected</c> exactly as it does when a socket dies. The
/// steps that are a socket's - the pipe reader, the close frame - have no counterpart and are
/// simply absent.
/// </para>
/// </remarks>
public sealed class HttpPrinterSessions : BackgroundService
{
    /// <summary>
    /// How often the reaper looks. Half the idle window, so a printer that has gone is noticed within
    /// one and a half windows at worst - and the check is a dictionary walk, so more often costs
    /// nothing worth saving.
    /// </summary>
    private static readonly TimeSpan ReapInterval = HttpPrinterConnection.IdleWindow / 2;

    private static long _lastSessionId;

    private readonly PrinterConnectionRegistry _registry;
    private readonly PrinterConnectionActorFactory _actorFactory;
    private readonly QueueSignal _queueSignal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HttpPrinterSessions> _logger;
    private readonly Dictionary<int, Session> _sessions = new();
    private readonly Lock _lock = new();

    public HttpPrinterSessions(PrinterConnectionRegistry registry,
                               PrinterConnectionActorFactory actorFactory,
                               QueueSignal queueSignal,
                               TimeProvider timeProvider,
                               ILogger<HttpPrinterSessions> logger)
    {
        _registry = registry;
        _actorFactory = actorFactory;
        _queueSignal = queueSignal;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// How long to wait for a reaped actor to finish draining. Same bound, for the same reason, as
    /// <see cref="PrinterConnectionSession.ActorDrainTimeout"/>.
    /// </summary>
    public TimeSpan ActorDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The actor for a printer that just made an authenticated request - created and registered on
    /// its first, touched on every one after.
    /// </summary>
    /// <remarks>
    /// The touch is inside the lock, not before it. Outside, the reaper could remove the session
    /// between the touch and the return, and the caller would post to an actor being drained - the
    /// exact race the lock exists to close.
    /// </remarks>
    public IPrinterConnectionActor GetOrCreate(int printerId, string? userAgent = null)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(printerId, out Session? existing))
            {
                existing.Connection.Touch();
                existing.Connection.Announce(userAgent);

                return existing.Actor;
            }

            // Opened before Create, and the ordering is the same trick PrinterConnectionSession
            // documents: the actor's loop starts in its constructor and captures the logging scope
            // then, so a scope opened afterwards would never reach it. Kept for the actor's whole
            // life - which is why the disposable lives on the Session rather than in a using here.
            IDisposable? correlation = _logger.BeginScope(new Dictionary<string, object>
            {
                ["PrinterId"] = printerId,
                ["ConnectionId"] = Interlocked.Increment(ref _lastSessionId),
            });

            HttpPrinterConnection connection = new(_timeProvider);
            connection.Announce(userAgent);
            IPrinterConnectionActor actor = _actorFactory.Create(printerId, connection);

            _sessions[printerId] = new Session(connection, actor, correlation);
            _registry.Register(printerId, actor);

            // Says what connected, not just that something did: Homespool could not previously
            // answer "what is this printer running", which notes/diagnostics.md lists as a blind spot.
            _logger.LogInformation("Printer {PrinterId} connected over the HTTP transport ({Client}).",
                                   printerId,
                                   connection.Client.Describe);

            // The dialect is logged separately from the client, because they answer different
            // questions: the client is what it said, and the dialect is what that means for how a
            // file can reach it. A support question is usually about the second.
            _logger.LogDebug("Printer {PrinterId} speaks {Dialect}.",
                             printerId,
                             PrinterDialect.For(connection.Client, supportsInlineTransfer: false).Name);

            // Louder, because this is the case where the guess is most likely wrong. An agent we do
            // not recognise is treated as Buddy - the safe default - but if it is in fact something
            // new that cannot decrypt a download, its transfers will time out with nothing else said.
            if (connection.Client.IsUnrecognised)
            {
                _logger.LogWarning(
                    "Printer {PrinterId} announced itself as {UserAgent}, which is not a client this "
                    + "server recognises. It is being treated as Buddy firmware, so it will be offered "
                    + "an encrypted download; if its transfers time out, that assumption is why.",
                    printerId,
                    connection.Client.UserAgent);
            }

            // Same as a socket arriving: there may be work waiting for it. The signal cannot fail
            // and carries nothing - the advancer re-reads everything.
            _queueSignal.Poke();

            return actor;
        }
    }

    /// <summary>
    /// Ends every session, in the same order the reaper would. Called by the host at shutdown, so
    /// no parked command is left with a caller awaiting it forever.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await ReapAsync(reapAll: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The service host's cancellation is the only thing that ends this. A tick that throws must
        // not: an exception here would silently stop reaping for the process's lifetime, and every
        // gone printer would leak an actor and hold its parked command forever.
        using PeriodicTimer timer = new(ReapInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ReapAsync(reapAll: false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Reaping HTTP printer sessions failed; will retry next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. StopAsync does the final reap.
        }
    }

    /// <summary>
    /// Removes and tears down every session whose printer has gone quiet - or all of them.
    /// </summary>
    private async Task ReapAsync(bool reapAll)
    {
        List<(int printerId, Session session)> gone = [];

        lock (_lock)
        {
            foreach ((int printerId, Session session) in _sessions)
            {
                if (reapAll || !session.Connection.IsOpen)
                {
                    gone.Add((printerId, session));
                }
            }

            foreach ((int printerId, _) in gone)
            {
                _sessions.Remove(printerId);
            }
        }

        // Teardown outside the lock: it awaits, and nothing about it needs the map any more - the
        // sessions are already gone from it, so no POST can reach them.
        foreach ((int printerId, Session session) in gone)
        {
            await TearDownAsync(printerId, session, reapAll ? "shutting down" : "idle");
        }
    }

    private async Task TearDownAsync(int printerId, Session session, string reason)
    {
        using (session.Correlation)
        {
            _logger.LogInformation("Printer {PrinterId} disconnected from the HTTP transport: {Reason}.", printerId, reason);

            // The socket session's ordering, for the reasons it gives: out of the registry first so
            // nothing can park a new command here, then complete so the loop drains and fails whatever
            // is parked as NotConnected, then a bounded wait so a wedged loop cannot hold the reaper.
            _registry.Unregister(printerId, session.Actor);
            session.Actor.Complete();

            try
            {
                await session.Actor.Completion.WaitAsync(ActorDrainTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("HTTP session actor did not finish draining in time; abandoning it.");
            }
            catch (Exception e)
            {
                _logger.LogError(e, "HTTP session actor loop faulted.");
            }
        }
    }

    private sealed record Session(HttpPrinterConnection Connection, IPrinterConnectionActor Actor, IDisposable? Correlation);
}
