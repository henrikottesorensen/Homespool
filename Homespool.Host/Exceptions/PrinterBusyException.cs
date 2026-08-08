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
        : base(BuildMessage(status))
    {
        Status = status;
    }

    /// <summary>The state that refused it.</summary>
    public PrinterStatus Status { get; }

    /// <summary>
    /// The sentence for a state, which is not one sentence.
    /// </summary>
    /// <remarks>
    /// <b>Not knowing is a different answer from being busy</b> (Henrik, 2026-08-08). A freshly
    /// connected printer reports <c>Unknown</c> until its first telemetry has been merged, so the
    /// refusal there is temporary and resolves on its own - where every other state in this list is
    /// the printer actually doing something the caller should not interrupt. Telling someone their
    /// printer "is Unknown" describes our own gap as if it were the machine's condition, and invites
    /// them to go and look at a printer that is perfectly fine.
    /// </remarks>
    private static string BuildMessage(PrinterStatus status)
    {
        return status is PrinterStatus.Unknown or PrinterStatus.Undefined
            ? "The printer's current state isn't known yet - it reports one shortly after connecting."
            : $"The printer is {status} - heaters can only be changed when it is idle, ready, "
              + "finished or stopped.";
    }
}
