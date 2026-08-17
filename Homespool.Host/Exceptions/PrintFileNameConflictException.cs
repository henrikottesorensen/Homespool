using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A file of that name already exists for this user, and the caller did not ask to replace it.
/// </summary>
/// <remarks>
/// Overwriting is deliberately opt-in rather than the default (<c>notes/file-storage.md</c>): a
/// re-slice legitimately produces the same name with new content, but so does an accident, and only
/// one of the two should be silent. OctoPrint overwrites silently; we make the caller say so.
/// </remarks>
public class PrintFileNameConflictException : Exception, ILocalisableError
{
    public PrintFileNameConflictException(string fileName)
        : base($"A file named '{fileName}' already exists. Ask to overwrite it if that is intended.")
    {
        FileName = fileName;
    }

    public PrintFileNameConflictException()
        : base("A file of that name already exists.")
    {
    }

    public PrintFileNameConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The name that collided, when the exception was raised for a specific one.</summary>
    public string? FileName { get; }

    /// <inheritdoc />
    public string ResourceKey =>
        FileName is null ? "Error_FileNameConflictAny" : "Error_FileNameConflict";

    /// <inheritdoc />
    public object[] ResourceArguments => FileName is null ? [] : [FileName];
}
