using System;
using System.Collections.Generic;

using Serilog;
using Serilog.Configuration;
using Serilog.Core;

namespace Homespool.Host.Services;

/// <summary>Puts <see cref="PrintableExceptionSink"/> in front of whatever sinks are configured inside it.</summary>
public static class PrintableLoggerConfigurationExtensions
{
    /// <summary>
    /// Writes to the sinks <paramref name="configure"/> adds, each event's exception made printable
    /// first.
    /// </summary>
    /// <remarks>
    /// The shape Serilog's own wrappers have - <c>WriteTo.Async(a =&gt; a.File(...))</c> - so it goes
    /// round any sink, several at once, and can be named from configuration the way those can.
    /// </remarks>
    public static LoggerConfiguration WithPrintableExceptions(this LoggerSinkConfiguration sinks,
                                                              Action<LoggerSinkConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(configure);

        return sinks.Sink(LoggerSinkConfiguration.Wrap(wrapped => new PrintableExceptionSink(wrapped), configure));
    }

    /// <summary>
    /// <c>ReadFrom.Services</c>, for everything the container holds except its sinks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ReadFrom.Services</c> writes to the container's sinks directly, which puts them beside
    /// <see cref="WithPrintableExceptions"/> rather than behind it. This leaves them for the caller
    /// to add inside the wrapper, and changes nothing else: the container's enrichers, filters,
    /// destructuring policies, settings and level switch are read by Serilog's own code, as before.
    /// </para>
    /// <para>
    /// Done by answering the one question differently - "which sinks are there?" - rather than by
    /// copying what <c>ReadFrom.Services</c> does, so whatever it learns to read later comes along.
    /// </para>
    /// </remarks>
    public static LoggerConfiguration ServicesExceptSinks(this LoggerSettingsConfiguration settings, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(services);

        return settings.Services(new WithoutSinks(services));
    }

    private sealed class WithoutSinks(IServiceProvider services) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IEnumerable<ILogEventSink>) ?
                Array.Empty<ILogEventSink>() :
                services.GetService(serviceType);
        }
    }
}
