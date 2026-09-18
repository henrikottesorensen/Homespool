using System;
using System.Linq;
using System.Threading.Tasks;

using Serilog.Core;
using Serilog.Events;

namespace Homespool.Host.Services;

/// <summary>
/// Wraps any sink, and hands it each event with its exception's text made printable - the one part
/// of an event <see cref="PrintableLogEnricher"/> cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sink.</b> <see cref="LogEvent.Exception"/> cannot be set, so nothing earlier in the
/// pipeline can change it; an event can only be issued again with another exception in its place,
/// and the last point that can do that for a sink is whatever stands directly in front of it.
/// </para>
/// <para>
/// <b>Every event still arrives.</b> Withholding one that carries an unprintable character would
/// let whoever can plant the character make an error line disappear, which is a better trick than
/// forging one. Replaced, never dropped - the event as well as the character.
/// </para>
/// <para>
/// <b>An event whose exception is already printable is forwarded untouched</b>, the same object, so
/// the wrapped sink sees the real exception type in every ordinary case. Only a dirty one arrives
/// as a <see cref="PrintableException"/>, with the original one step down as its inner exception.
/// </para>
/// </remarks>
public sealed class PrintableExceptionSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private readonly ILogEventSink _wrapped;

    public PrintableExceptionSink(ILogEventSink wrapped)
    {
        ArgumentNullException.ThrowIfNull(wrapped);

        _wrapped = wrapped;
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        _wrapped.Emit(Printable(logEvent));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        (_wrapped as IDisposable)?.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_wrapped is IAsyncDisposable asynchronous)
        {
            return asynchronous.DisposeAsync();
        }

        Dispose();

        return ValueTask.CompletedTask;
    }

    private static LogEvent Printable(LogEvent logEvent)
    {
        if (logEvent.Exception is not { } exception)
        {
            return logEvent;
        }

        string text = exception.ToString();
        string printable = PrintableLogEnricher.Printable(text, keepLineBreaks: true);

        if (ReferenceEquals(printable, text))
        {
            return logEvent;
        }

        return new LogEvent(logEvent.Timestamp,
                            logEvent.Level,
                            new PrintableException(exception, printable),
                            logEvent.MessageTemplate,
                            logEvent.Properties.Select(property => new LogEventProperty(property.Key, property.Value)),
                            logEvent.TraceId ?? default,
                            logEvent.SpanId ?? default);
    }
}
