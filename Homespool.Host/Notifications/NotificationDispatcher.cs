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
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(NotificationQueue queue,
                                  IServiceScopeFactory scopes,
                                  ILogger<NotificationDispatcher> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (PrinterHappening happening in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

                    int told = await scope.ServiceProvider.GetRequiredService<NotificationRouter>()
                                          .SendAsync(happening, stoppingToken);

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
