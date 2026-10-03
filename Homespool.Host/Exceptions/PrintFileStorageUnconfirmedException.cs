using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The print-file storage has not been confirmed to be the real one, so nothing may be written to it.
/// </summary>
/// <remarks>
/// The root is deliberately not in the sentence a reader sees: whoever uploads is not necessarily
/// whoever can fix it, and a container path is no use to them. It is in the log.
/// </remarks>
public class PrintFileStorageUnconfirmedException : Exception, ILocalisableError
{
    public PrintFileStorageUnconfirmedException()
        : base("Print-file storage has not been confirmed, so nothing was written.")
    {
    }

    public PrintFileStorageUnconfirmedException(string message)
        : base(message)
    {
    }

    public PrintFileStorageUnconfirmedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Error_PrintFileStorageUnconfirmed";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
