using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A printer sent more bytes than <c>PrusaConnect:MaxIncomingMessageBytes</c> allows without
/// completing a JSON document.
/// </summary>
/// <remarks>
/// <para>
/// A protocol violation in the same class as malformed JSON, and closed on the same way - but with
/// <c>MessageTooBig</c> rather than <c>PolicyViolation</c>, because the WebSocket vocabulary has a
/// status that says exactly this and a printer's logs are easier to read when it is used.
/// </para>
/// <para>
/// <b>The cap is a judgement, not a protocol fact</b>, so this carries the numbers: the message is
/// what a person needs to decide whether a real printer tripped it or an abusive client did. See
/// <c>notes/protocol-reference.md</c>, "Message size and fragmentation".
/// </para>
/// </remarks>
public class PrinterMessageTooLargeException : Exception
{
    public PrinterMessageTooLargeException()
    {
    }

    public PrinterMessageTooLargeException(string message)
        : base(message)
    {
    }

    public PrinterMessageTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PrinterMessageTooLargeException(int printerId, long bufferedBytes, long limitBytes)
        : base($"Printer {printerId} buffered {bufferedBytes} bytes without completing a message; "
               + $"the limit is {limitBytes}.")
    {
        PrinterId = printerId;
        BufferedBytes = bufferedBytes;
        LimitBytes = limitBytes;
    }

    /// <summary>The printer whose connection this was.</summary>
    public int PrinterId { get; }

    /// <summary>How much had accumulated when the limit was passed.</summary>
    public long BufferedBytes { get; }

    /// <summary>The configured limit, so the message says what to change.</summary>
    public long LimitBytes { get; }
}
