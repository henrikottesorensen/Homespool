using System;

namespace Homespool.Host.Exceptions;

/// <summary>
/// The printer has not said what filament is loaded, so it cannot be unloaded from here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a caution - a hard precondition.</b> Firmware picks the unload temperature by reading its
/// own stored filament type, and when there is none it opens a dialog on the panel and waits for
/// somebody to answer it (<c>evaluate_preheat_conditions</c>, <c>M70X_preheat.cpp:201-227</c>).
/// Sending the command anyway would leave a machine blocked on a prompt nobody is standing at, which
/// is the same trap <c>gcode-allowlist.md</c> keeps <c>M997</c>'s neighbour <c>M1700</c> off the list
/// for.
/// </para>
/// <para>
/// In practice this is a printer with nothing loaded, which is also why the control is absent rather
/// than refusing on the page: there is nothing to unload. This exists for the post that arrives
/// anyway - a button that is not rendered is not a check.
/// </para>
/// </remarks>
public class FilamentTypeUnknownException : Exception, ILocalisableError
{
    /// <summary>The one callers actually use.</summary>
    public FilamentTypeUnknownException(int printerId)
        : base($"Printer {printerId} has not reported a loaded filament type.")
    {
    }

    // The three constructors every public exception type is expected to carry (CA1032).
    public FilamentTypeUnknownException()
    {
    }

    public FilamentTypeUnknownException(string message)
        : base(message)
    {
    }

    public FilamentTypeUnknownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public string ResourceKey => "Error_FilamentTypeUnknown";

    /// <inheritdoc />
    public object[] ResourceArguments => [];
}
