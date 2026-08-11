using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using Serilog.Core;
using Serilog.Events;

namespace Homespool.Host.E2ETest;

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
/// reasoning <c>Homespool.Host.Test</c>'s <c>PrinterRegistrationTests.CapturingSink</c> (a
/// separate, private nested class in that project) uses for its own assertions, just exposed by
/// property name here instead of by substring.
/// </remarks>
public sealed class CapturingSink : ILogEventSink
{
    private readonly ConcurrentBag<LogEvent> _events = new();

    public void Emit(LogEvent logEvent)
    {
        _events.Add(logEvent);
    }

    /// <summary>
    /// Every event at <see cref="LogEventLevel.Error"/> or above. A request that completes normally
    /// should produce none, which makes this the assertion for "the server handled that without
    /// anything going wrong behind the scenes" - errors thrown after the response has started are
    /// invisible to the client and show up nowhere else.
    /// </summary>
    public IReadOnlyList<LogEvent> Failures =>
        _events.Where(e => e.Level >= LogEventLevel.Error).ToList();

    /// <summary>
    /// The first scalar value logged under <paramref name="propertyName"/>, or <c>null</c> if nothing
    /// matches. Strips the surrounding quotes <see cref="ScalarValue"/>'s default rendering adds for
    /// strings.
    /// </summary>
    public string? FindPropertyValue(string propertyName)
    {
        return _events
               .SelectMany(e => e.Properties)
               .Where(p => p.Key == propertyName)
               .Select(p => p.Value is ScalarValue { Value: string s } ? s : p.Value.ToString())
               .FirstOrDefault();
    }
}
