using System;
using System.Globalization;

namespace Homespool.Host.Exceptions;

/// <summary>
/// An object was named that the running print has not declared cancellable.
/// </summary>
/// <remarks>
/// <b>Checked against what the printer last reported, not passed through.</b> Object ids reach this
/// application from a form post, and the printer's own count is the only authority on which ones
/// exist. The message counts from one, as the page and the printer's menu do; the id underneath does
/// not.
/// </remarks>
public class NoSuchObjectException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public NoSuchObjectException(int printerId, int objectId)
        : base($"Printer {printerId}'s print has no cancellable object {objectId}.")
    {
        ObjectId = objectId;
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public NoSuchObjectException()
    {
    }

    public NoSuchObjectException(string message)
        : base(message)
    {
    }

    public NoSuchObjectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The object that was asked for, 0-based.</summary>
    public int ObjectId { get; }

    /// <inheritdoc />
    public string ResourceKey => "Error_NoSuchObject";

    /// <inheritdoc />
    public object[] ResourceArguments => [(ObjectId + 1).ToString(CultureInfo.InvariantCulture)];
}
