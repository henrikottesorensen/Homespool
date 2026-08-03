using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The caller has no file by that name.
/// </summary>
/// <remarks>
/// <b>"Someone else's file" and "no such file" are the same answer</b>, because every lookup is scoped
/// to a user id and neither case can confirm the other's existence. That is the ownership check, and
/// it is structural rather than a comparison somebody has to remember to write.
/// </remarks>
public class PrintFileNotFoundException : Exception
{
    public PrintFileNotFoundException(string fileName)
        : base($"You have no file named '{fileName}'.")
    {
        FileName = fileName;
    }

    public PrintFileNotFoundException()
        : base("You have no file by that name.")
    {
    }

    public PrintFileNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The name that was asked for, when the exception was raised for a specific one.</summary>
    public string? FileName { get; }
}
