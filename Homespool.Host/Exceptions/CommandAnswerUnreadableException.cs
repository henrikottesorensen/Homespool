using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The printer answered a question with a payload that will not parse into the shape the command
/// declared - so the command worked, and what came back cannot be read.
/// </summary>
/// <remarks>
/// <para>
/// A firmware release adding a field cannot cause this: unknown members are ignored, as everywhere
/// else on this path. What causes it is a field whose <i>type</i> moved, or a payload that is not
/// the shape the command's <c>TAnswer</c> claims - which makes this a signal worth surfacing rather
/// than swallowing, and the reason the answer is not simply returned as null.
/// </para>
/// <para>
/// The event itself is unaffected and is persisted verbatim like any other, so the payload that
/// failed here is still on record in <c>PrinterEvents</c> and can be read back.
/// </para>
/// </remarks>
public class CommandAnswerUnreadableException : Exception
{
    /// <summary>The one callers actually use.</summary>
    public CommandAnswerUnreadableException(int printerId, string wireName, Exception innerException)
        : base($"Printer {printerId} answered {wireName} with a payload that could not be read.", innerException)
    {
        PrinterId = printerId;
        WireName = wireName;
    }

    // The three constructors every public exception type is expected to carry (CA1032). See
    // PrinterNotConnectedException for why they are here despite nothing calling them.
    public CommandAnswerUnreadableException()
    {
    }

    public CommandAnswerUnreadableException(string message)
        : base(message)
    {
    }

    public CommandAnswerUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The printer whose answer could not be read, when raised for a specific one.</summary>
    public int? PrinterId { get; }

    /// <summary>The command that was asked, e.g. <c>SEND_FILE_INFO</c>.</summary>
    public string? WireName { get; }
}
