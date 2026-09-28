using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The file was sliced for a machine this printer must not be asked to imitate.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one compatibility finding that refuses rather than warns</b>, and the reason is that
/// nothing clears it. Every other hold is a condition on the printer that a person can
/// go and change - fit a hardened nozzle, free some space - so the entry is worth keeping while they
/// do. A bed slinger does not become a CoreXY, so an entry queued on this one would wait for an event
/// that cannot happen, and the queue behind it waits too.
/// </para>
/// <para>
/// <b>Raised before the row is written</b>, so a refused queue leaves nothing behind to withdraw.
/// </para>
/// </remarks>
public class IncompatiblePrinterModelException : Exception, ILocalisableError
{
    public IncompatiblePrinterModelException(string fileName, string? fileModel, string? printerModel)
        : base($"'{fileName}' is sliced for {fileModel} and this printer is a {printerModel}.")
    {
        FileName = fileName;
        FileModel = fileModel;
        PrinterModel = printerModel;
    }

    public IncompatiblePrinterModelException()
        : base("That file was sliced for a different printer.")
    {
    }

    public IncompatiblePrinterModelException(string message)
        : base(message)
    {
    }

    public IncompatiblePrinterModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The file that was refused.</summary>
    public string? FileName { get; }

    /// <summary>The model the slicer wrote into the file, as it wrote it.</summary>
    public string? FileModel { get; }

    /// <summary>The printer's own model, resolved to a designation rather than the reported triple.</summary>
    public string? PrinterModel { get; }

    /// <inheritdoc />
    /// <remarks>
    /// One sentence, with no nameless twin: the constructors that leave the file unnamed exist for the
    /// analyser's sake, and nothing raises this without knowing the file and both models.
    /// </remarks>
    public string ResourceKey => "Error_IncompatiblePrinterModel";

    /// <inheritdoc />
    public object[] ResourceArguments => [FileName ?? string.Empty, FileModel ?? string.Empty, PrinterModel ?? string.Empty];
}
