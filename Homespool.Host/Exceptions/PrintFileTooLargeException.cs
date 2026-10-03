using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// A file is too large for a printer to be sent: its size cannot be described to firmware, which
/// takes it as an unsigned 32-bit number.
/// </summary>
/// <remarks>
/// Not a fault in the request that can be mended by retrying - the same file is just as large the
/// next time - so a sender that is not a person at a page holds on it rather than trying again.
/// </remarks>
public class PrintFileTooLargeException : Exception, ILocalisableError
{
    public PrintFileTooLargeException()
        : base("Files must be under 4 GiB - a printer cannot be sent anything larger.")
    {
    }

    public PrintFileTooLargeException(string message)
        : base(message)
    {
    }

    public PrintFileTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Files_OverFourGiB";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
