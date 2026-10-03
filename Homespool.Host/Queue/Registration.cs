using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Homespool.Host.Queue;

/// <summary>
/// Wires up what the queue's passes share, so <c>Program.cs</c> says what is being added rather than how.
/// </summary>
public static class Registration
{
    /// <summary>
    /// Adds the <see cref="QueueWorkBudget"/> - one permit per processor unless one is registered
    /// already - and the <see cref="QueuePassWork"/> each pass claims it through.
    /// </summary>
    public static IServiceCollection AddQueueWork(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ => new QueueWorkBudget(Environment.ProcessorCount));
        services.AddScoped<QueuePassWork>();

        return services;
    }
}
