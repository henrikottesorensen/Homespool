using System.Collections.Concurrent;
using System.Linq;

using Serilog.Core;
using Serilog.Events;

namespace PrinterService.Host.Test;

/// <summary>
/// A Serilog <see cref="ILogEventSink"/> that records every event, registered in DI so
/// <c>Program.cs</c>'s <c>.ReadFrom.Services(services)</c> picks it up automatically alongside the
/// console sink - a bare <c>Microsoft.Extensions.Logging.ILoggerProvider</c> registered the same way
/// does <b>not</b> work here, because <c>AddSerilog</c> replaces <c>ILoggerFactory</c> with a bridge
/// to the one configured Serilog pipeline rather than fanning out to independently-registered
/// logging providers.
/// </summary>
/// <remarks>
/// Reads structured properties directly off the captured <see cref="LogEvent"/> rather than the
/// rendered message string, so a lookup doesn't depend on message wording or whitespace - the same
/// reasoning <c>PrinterRegistrationTests.CapturingSink</c> uses for its own assertions, just exposed
/// by property name here instead of by substring.
/// </remarks>
public sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentBag<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Add(logEvent);

    /// <summary>
    /// The first scalar value logged under <paramref name="propertyName"/>, or <c>null</c> if nothing
    /// matches. Strips the surrounding quotes Serilog's default <see cref="ScalarValue.ToString()"/>
    /// adds for strings.
    /// </summary>
    public string? FindPropertyValue(string propertyName) =>
        _events
            .SelectMany(e => e.Properties)
            .Where(p => p.Key == propertyName)
            .Select(p => p.Value is ScalarValue { Value: string s } ? s : p.Value.ToString())
            .FirstOrDefault();
}
