using System;

namespace Homespool.Host.Services;

/// <summary>
/// Stands in for an exception on its way to a log sink: everything a sink writes about it reads as
/// the original's, with the characters a reader cannot see replaced.
/// </summary>
/// <remarks>
/// <para>
/// An exception's message is composed from whatever was in hand when it was thrown, and that is
/// often somebody else's string - a file name, a header, a field off the wire. A sink writes
/// <see cref="ToString"/>, so that is what is made printable, the inner exceptions and the trace
/// with it. Line breaks are kept: a trace is made of them.
/// </para>
/// <para>
/// <b>The original is the <see cref="Exception.InnerException"/></b>, so nothing is lost to a sink
/// that goes looking - it finds the real type, the real data and the real trace one step down.
/// </para>
/// </remarks>
public sealed class PrintableException : Exception
{
    private readonly string? _text;

    public PrintableException()
    {
    }

    public PrintableException(string message)
        : base(message)
    {
    }

    public PrintableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Wraps <paramref name="original"/>, whose printable text the caller has already worked out.</summary>
    internal PrintableException(Exception original, string printableText)
        : base(PrintableLogEnricher.Printable(original.Message, keepLineBreaks: true), original)
    {
        _text = printableText;
    }

    /// <inheritdoc />
    public override string? StackTrace => InnerException?.StackTrace ?? base.StackTrace;

    /// <summary>The original's <see cref="Exception.ToString"/>, made printable.</summary>
    public override string ToString()
    {
        return _text ?? base.ToString();
    }
}
