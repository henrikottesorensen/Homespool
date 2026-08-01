using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A file was found a moment ago and could not be opened now, which in practice means a delete
/// racing a send rather than anything the caller got wrong.
/// </summary>
public class PrintFileUnreadableException : Exception
{
    public PrintFileUnreadableException(string fileName)
        : base($"{fileName} could not be read - it may have just been deleted.")
    {
        FileName = fileName;
    }

    public PrintFileUnreadableException()
        : base("That file could not be read - it may have just been deleted.")
    {
    }

    public PrintFileUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The file that could not be opened, when the exception was raised for a specific one.</summary>
    public string? FileName { get; }
}
