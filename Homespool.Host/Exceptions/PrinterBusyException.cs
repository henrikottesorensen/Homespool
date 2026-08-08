using System;

using Homespool.Model;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The printer is in a state where the requested change would interfere with what it is doing.
/// </summary>
/// <remarks>
/// Carries the state so the answer can name it. "The printer is busy" sends someone to look at the
/// machine; "the printer is printing" tells them why without moving.
/// </remarks>
public class PrinterBusyException : Exception
{
    public PrinterBusyException()
    {
    }

    public PrinterBusyException(string message)
        : base(message)
    {
    }

    public PrinterBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PrinterBusyException(PrinterStatus status)
        : base($"The printer is {status} - heaters can only be changed when it is idle, ready, "
               + "finished or stopped.")
    {
        Status = status;
    }

    /// <summary>The state that refused it.</summary>
    public PrinterStatus Status { get; }
}
