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
public class PrinterBusyException : Exception, ILocalisableError
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

    /// <inheritdoc />
    public string ResourceKey =>
        Status is PrinterStatus.Unknown or PrinterStatus.Undefined ?
            "Error_PrinterStateUnknown" :
            "Error_PrinterBusy";

    /// <summary>
    /// The status itself, not its name - so whoever renders it can put it through the display seam.
    /// </summary>
    /// <remarks>
    /// Handing over <c>Status.ToString()</c> here would hard-code the English name into a Danish
    /// sentence, which is the one thing <see cref="Localisation.PrinterStatusText"/> exists to
    /// prevent. Passing the enum keeps that decision where the localiser is.
    /// </remarks>
    public object[] ResourceArguments => [Status];

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
    /// <para>
    /// <b>It names no particular operation</b>, because more than one raises this: heating was the
    /// first and unloading filament is the second, and both are refused by the same rule in
    /// <see cref="Printing.PhysicalChangeRules"/>. A sentence about heater targets shown to somebody
    /// who pressed Unload describes the wrong thing.
    /// </para>
    /// </remarks>
    private static string BuildMessage(PrinterStatus status)
    {
        return status is PrinterStatus.Unknown or PrinterStatus.Undefined ?
            "The printer's current state isn't known yet - it reports one shortly after connecting." :
            $"The printer is {status} - this can only be done when the printer is not busy.";
    }
}
