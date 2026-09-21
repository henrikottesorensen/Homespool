using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// Somebody asked to print a print again, and the file under that name is no longer the bytes it
/// printed.
/// </summary>
/// <remarks>
/// <b>A question rather than a refusal.</b> Re-slicing under the same name is ordinary, and printing the
/// new version may be exactly what was wanted - so the caller can ask again and say so. What must not
/// happen is printing it unasked: the queue starts a print within seconds of a ready printer, so a
/// warning given afterwards arrives after the plastic.
/// </remarks>
public class PrintFileChangedException : Exception, ILocalisableError
{
    public PrintFileChangedException(string fileName)
        : base($"'{fileName}' has changed since the last print.")
    {
        FileName = fileName;
    }

    public PrintFileChangedException()
        : base("The file has changed since the last print.")
    {
    }

    public PrintFileChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The file's name as the print recorded it.</summary>
    public string? FileName { get; }

    /// <inheritdoc />
    public string ResourceKey => "Printers_ReprintChanged";

    /// <inheritdoc />
    public object[] ResourceArguments => [FileName ?? string.Empty];
}
