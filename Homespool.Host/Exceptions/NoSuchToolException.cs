using System;
using System.Globalization;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A tool was named that this printer has not reported.
/// </summary>
/// <remarks>
/// <b>Refused rather than passed through to firmware.</b> Firmware would decline a tool that is not
/// enabled - but a number naming a <em>different</em> fitted head would be obeyed, and unloading the
/// wrong spool is not something anything downstream can catch. Tool numbers reach this application
/// from a form post, so the printer's own list is the only authority worth checking against.
/// </remarks>
public class NoSuchToolException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public NoSuchToolException(int printerId, int toolNumber)
        : base($"Printer {printerId} has not reported a tool {toolNumber}.")
    {
        ToolNumber = toolNumber;
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public NoSuchToolException()
    {
    }

    public NoSuchToolException(string message)
        : base(message)
    {
    }

    public NoSuchToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The tool that was asked for.</summary>
    public int ToolNumber { get; }

    /// <inheritdoc />
    public string ResourceKey => "Error_NoSuchTool";

    /// <inheritdoc />
    public object[] ResourceArguments => [ToolNumber.ToString(CultureInfo.InvariantCulture)];
}
