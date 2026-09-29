using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Notifications;

/// <summary>
/// Takes each happening off <see cref="NotificationQueue"/> and has it routed and delivered, one at a
/// time.
/// </summary>
/// <remarks>
/// <b>One at a time, deliberately.</b> A deployment of tens of printers produces a handful of these an
/// hour, and a push service answers in well under a second, so there is nothing to gain from overlap
/// and a good deal to lose: deliveries in parallel would race each other's bookkeeping on the same
/// destination row. A scope per happening, as the queue loop takes one per pass.
/// </remarks>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly NotificationQueue _queue;
    private readonly NotificationThrottle _throttle;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(NotificationQueue queue,
                                  NotificationThrottle throttle,
                                  IServiceScopeFactory scopes,
                                  TimeProvider time,
                                  ILogger<NotificationDispatcher> logger)
    {
        _queue = queue;
        _throttle = throttle;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (PrinterHappening happening in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                // Here rather than at each source, so no pattern of changes a printer can produce -
                // and no source added later - gets past it.
                if (!_throttle.Allows(happening, _time.GetUtcNow()))
                {
                    _logger.LogInformation("[{PrinterId}] {Happening} not sent: this printer sent one moments ago.",
                                           happening.PrinterId, happening.GetType().Name);

                    continue;
                }

                try
                {
                    await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

                    int told = await scope.ServiceProvider.GetRequiredService<NotificationRouter>()
                                          .SendAsync(happening, stoppingToken);

                    if (told > 0)
                    {
                        _throttle.Sent(happening, _time.GetUtcNow());
                    }

                    _logger.LogInformation("[{PrinterId}] {Happening}: {People} person(s) notified.",
                                           happening.PrinterId, happening.GetType().Name, told);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One notification's failure is not the next one's.
                    _logger.LogError(e, "[{PrinterId}] sending a {Happening} notification failed.",
                                     happening.PrinterId, happening.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
