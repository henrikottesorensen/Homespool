using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A toolchanger was asked to unload without being told which tool.
/// </summary>
/// <remarks>
/// <b>There is no defensible default, which is why this is an error rather than a fallback.</b>
/// Choosing the first fitted head, or whichever is on the carriage, would be guessing at somebody's
/// spool - and <c>M702</c> now carries an explicit <c>T</c> precisely so that guess is never needed.
/// The picker dialog exists to make the choice, so reaching this means a post that bypassed it.
/// </remarks>
public class ToolNotSpecifiedException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public ToolNotSpecifiedException(int printerId)
        : base($"Printer {printerId} has several tools, so an unload must name one.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public ToolNotSpecifiedException()
    {
    }

    public ToolNotSpecifiedException(string message)
        : base(message)
    {
    }

    public ToolNotSpecifiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Error_ToolNotSpecified";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
