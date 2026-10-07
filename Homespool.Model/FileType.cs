namespace Homespool.Model;

/// <summary>
/// What an <see cref="Entities.HSFile"/> is: which store holds its bytes, and so what may be done with
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decided by the store that wrote the row, never by the name or the bytes.</b> Anything in a
/// user's directory is G-code, whatever its extension says; a firmware image lives only in the
/// firmware store. Plain against binary G-code is deliberately not a type: the container is read from
/// a file's first bytes, a <c>.gcode</c> is routinely binary, and a row indexed on the way to a print
/// has not been read at all.
/// </para>
/// <para>
/// Stored by name, so a member may be added anywhere and none may be renamed.
/// </para>
/// </remarks>
public enum FileType
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>A file someone uploaded to print, held in their own directory.</summary>
    GCode = 1,

    /// <summary>
    /// A Prusa firmware image (<c>.bbf</c>), held in the firmware store and sent to a printer only to
    /// be flashed.
    /// </summary>
    PrusaFirmware = 2,
}
