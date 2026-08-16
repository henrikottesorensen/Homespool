using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The printer's connection speaks a protocol that has no way to express what was asked - a
/// wire-typed Prusa command or query sent to a link that is not the Prusa actor. Distinct from
/// <see cref="PrinterNotConnectedException"/>: the printer is connected; it is the request that
/// cannot travel this link.
/// </summary>
public class PrinterProtocolUnsupportedException : Exception
{
    /// <summary>The one callers actually use.</summary>
    /// <param name="printerId">The printer whose link refused.</param>
    /// <param name="request">What was asked, by its wire name, for the log.</param>
    public PrinterProtocolUnsupportedException(int printerId, string request)
        : base($"Printer {printerId}'s protocol has no way to express {request}.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032). Nothing in
    // this project calls them, but leaving them off makes the type awkward for anything that wants
    // to wrap or rethrow it with context of its own.
    public PrinterProtocolUnsupportedException()
    {
    }

    public PrinterProtocolUnsupportedException(string message)
        : base(message)
    {
    }

    public PrinterProtocolUnsupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
