using System;

using Microsoft.Extensions.DependencyInjection;

using Homespool.Host.Queue;
using Homespool.Host.Telemetry;

namespace Homespool.Host.Printing;

/// <summary>
/// Wires up file transfers, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
public static class Registration
{
    /// <summary>
    /// Adds <see cref="TransferService"/> - as itself, as a hosted service, and as the telemetry
    /// writer's event observer - and the queue's answer to its own transfers ending.
    /// </summary>
    /// <remarks>
    /// <b>Add it before the queue's advancer.</b> Hosted services stop in reverse order, and the
    /// advancer sends through this service, so this one has to outlive it.
    /// </remarks>
    public static IServiceCollection AddTransfers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // One instance in three roles: the mailboxes are process-wide, and the writer's nudges have to
        // reach the same ones the senders post to.
        services.AddSingleton<TransferService>();
        services.AddHostedService(provider => provider.GetRequiredService<TransferService>());
        services.AddSingleton<IPrinterEventObserver>(provider => provider.GetRequiredService<TransferService>());

        // Scoped: resolved per settle, in the settle's own scope.
        services.AddScoped<ITransferEndPolicy, QueueTransferEndings>();

        return services;
    }
}
