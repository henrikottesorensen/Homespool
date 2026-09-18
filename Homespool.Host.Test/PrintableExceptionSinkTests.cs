using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using AwesomeAssertions;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// A sink behind the wrapper is handed every event, and never an exception whose text holds a
/// character its reader cannot see.
/// </summary>
/// <remarks>
/// An exception's message is composed from whatever was in hand when it was thrown - a file name, a
/// header, a field off the wire - and it is the one part of an event no enricher can change. The
/// escapes are written rather than pasted, as in <c>LogTextTests</c>.
/// </remarks>
public class PrintableExceptionSinkTests
{
    private const string Dirty = "\u001B[2J\u0085\u202E\u2028";

    private const string Replaced = "\uFFFD[2J\uFFFD\uFFFD\uFFFD";

    [Fact]
    public void ADirtyExceptionArrivesPrintableInnerExceptionsAndAll()
    {
        Collecting collected = new();
        Exception thrown = Thrown("outer " + Dirty, new FormatException("inner " + Dirty));

        Emit(collected, Event(thrown));

        Exception arrived = collected.Events.Should().ContainSingle().Which.Exception!;
        arrived.ToString().Should().Contain("outer " + Replaced).And.Contain("inner " + Replaced);
        arrived.ToString().Should().NotContainAny("\u001B", "\u0085", "\u202E", "\u2028");
        arrived.Message.Should().Be("outer " + Replaced);
    }

    /// <summary>A trace is made of line breaks; replacing them would leave one line nobody can read.</summary>
    [Fact]
    public void TheTraceKeepsItsLineBreaks()
    {
        Collecting collected = new();
        Exception thrown = Thrown("outer " + Dirty, new FormatException("inner"));

        Emit(collected, Event(thrown));

        string text = collected.Events.Single().Exception!.ToString();
        text.Should().Contain("\u000A");
        text.Split('\u000A').Should().Contain(line => line.Contains(nameof(Thrown), StringComparison.Ordinal), "the frame that threw is still a line of its own");
    }

    /// <summary>Nothing is lost to a sink that goes looking: the real exception is one step down.</summary>
    [Fact]
    public void TheOriginalIsKeptAsTheInnerException()
    {
        Collecting collected = new();
        Exception thrown = Thrown("outer " + Dirty, null);

        Emit(collected, Event(thrown));

        Exception arrived = collected.Events.Single().Exception!;
        arrived.Should().BeOfType<PrintableException>();
        arrived.InnerException.Should().BeSameAs(thrown);
        arrived.StackTrace.Should().Be(thrown.StackTrace);
    }

    [Fact]
    public void TheRestOfTheEventIsCarriedOver()
    {
        Collecting collected = new();
        ActivityTraceId trace = ActivityTraceId.CreateRandom();
        ActivitySpanId span = ActivitySpanId.CreateRandom();
        LogEvent sent = new(DateTimeOffset.UnixEpoch.AddDays(1),
                            LogEventLevel.Warning,
                            Thrown(Dirty, null),
                            new MessageTemplateParser().Parse("printer {PrinterId}"),
                            [new LogEventProperty("PrinterId", new ScalarValue(7))],
                            trace,
                            span);

        Emit(collected, sent);

        LogEvent arrived = collected.Events.Single();
        arrived.Timestamp.Should().Be(sent.Timestamp);
        arrived.Level.Should().Be(LogEventLevel.Warning);
        arrived.MessageTemplate.Should().BeSameAs(sent.MessageTemplate);
        arrived.Properties.Should().ContainKey("PrinterId").WhoseValue.Should().BeSameAs(sent.Properties["PrinterId"]);
        arrived.TraceId.Should().Be(trace);
        arrived.SpanId.Should().Be(span);
    }

    [Fact]
    public void AnEventWithoutATraceStaysWithoutOne()
    {
        Collecting collected = new();

        Emit(collected, Event(Thrown(Dirty, null)));

        collected.Events.Single().TraceId.Should().BeNull();
        collected.Events.Single().SpanId.Should().BeNull();
    }

    /// <summary>
    /// The ordinary case is left entirely alone - the same event, so the sink sees the real exception
    /// type whenever there was nothing to replace.
    /// </summary>
    [Fact]
    public void AnEventWithNothingToReplaceIsForwardedAsItIs()
    {
        Collecting collected = new();
        LogEvent clean = Event(Thrown("Br\u00E4cket-2.gcode is not a print file", null));
        LogEvent bare = Event(null);

        using PrintableExceptionSink sink = new(collected);
        sink.Emit(clean);
        sink.Emit(bare);

        collected.Events.Should().HaveCount(2);
        collected.Events[0].Should().BeSameAs(clean);
        collected.Events[1].Should().BeSameAs(bare);
    }

    /// <summary>
    /// Replaced, never dropped - the event as well as the character. Withholding one would let
    /// whoever can plant the character make an error line disappear.
    /// </summary>
    [Fact]
    public void EveryEventArrives()
    {
        Collecting collected = new();
        using PrintableExceptionSink sink = new(collected);

        sink.Emit(Event(Thrown(Dirty, null)));
        sink.Emit(Event(Thrown("clean", null)));
        sink.Emit(Event(null));

        collected.Events.Should().HaveCount(3);
    }

    [Fact]
    public void DisposingItDisposesWhatItWraps()
    {
        using Disposable wrapped = new();

        using (new PrintableExceptionSink(wrapped))
        {
            wrapped.Disposed.Should().BeFalse();
        }

        wrapped.Disposed.Should().BeTrue();
    }

    // ---- through a pipeline, the way the host uses it ----
    [Fact]
    public void ItGoesRoundWhateverSinksAreConfiguredInsideIt()
    {
        Collecting first = new();
        Collecting second = new();

        using (Logger logger = new LoggerConfiguration()
                               .WriteTo.WithPrintableExceptions(sinks =>
                               {
                                   sinks.Sink(first);
                                   sinks.Sink(second);
                               })
                               .CreateLogger())
        {
            logger.Error(Thrown("outer " + Dirty, null), "it failed");
        }

        foreach (Collecting sink in new[] { first, second })
        {
            sink.Events.Should().ContainSingle()
                .Which.Exception!.ToString().Should().Contain("outer " + Replaced);
        }
    }

    /// <summary>Thrown and caught, so it has a trace like any exception that reaches a log.</summary>
    private static Exception Thrown(string message, Exception? inner)
    {
        try
        {
            throw new InvalidOperationException(message, inner);
        }
        catch (InvalidOperationException thrown)
        {
            return thrown;
        }
    }

    private static LogEvent Event(Exception? exception)
    {
        return new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Error, exception, new MessageTemplateParser().Parse("it failed"), []);
    }

    private static void Emit(Collecting collected, LogEvent logEvent)
    {
        using PrintableExceptionSink sink = new(collected);

        sink.Emit(logEvent);
    }

    private sealed class Collecting : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }

    private sealed class Disposable : ILogEventSink, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Emit(LogEvent logEvent)
        {
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
