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
    /// The sentence for a state - two of them, because the states mean different things to a reader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Unknown</c> is not the printer being busy: it is this application not having merged the
    /// printer's first telemetry yet, which resolves on its own within seconds of connecting. It
    /// therefore says <em>wait</em>, where every other state says <em>stop</em> - naming it as a
    /// printer condition would send someone to look at a machine that is fine.
    /// </para>
    /// <para>
    /// The busy sentence states the rule rather than listing <see cref="PrinterStatus"/> members,
    /// and says <b>busy</b> rather than <b>working</b>, since "not working" also means broken.
    /// </para>
    /// </remarks>
    private static string BuildMessage(PrinterStatus status)
    {
        return status is PrinterStatus.Unknown or PrinterStatus.Undefined
            ? "The printer's current state isn't known yet - it reports one shortly after connecting."
            : $"The printer is {status} - heaters can only be changed when the printer is not busy.";
    }
}
