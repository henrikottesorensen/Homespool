using System;

using Serilog;
using Serilog.Configuration;

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
}
