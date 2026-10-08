namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Arbitrary gcode, sent to the printer to execute. A hollow marker today — it is not
/// <c>ISendableCommand</c>, so nothing can send it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read this before making it sendable.</b> Doing so is an obvious and reasonable feature, and it
/// silently completes a privilege-escalation chain whose other half lives somewhere nobody would
/// think to look — <c>UserFileStore.AllowedExtensions</c>.
/// </para>
/// <para>
/// Firmware's <c>M997</c> reflashes the mainboard from a <c>.bbf</c> image named by short filename
/// under <c>/usb/</c> (<c>src/marlin_stubs/M997.cpp</c>). The application validates <b>nothing</b>: it
/// writes the name into the bootloader handoff region and resets. The bootloader checks Prusa's
/// signature only while the printer's appendix is intact; once it is broken, it flashes anything.
/// </para>
/// <para>
/// So <b>put a <c>.bbf</c> on the drive</b> + <b>send <c>M997</c></b> = flash arbitrary firmware on
/// someone's printer. Three guards stand in the way, and none of them may be loosened on the strength
/// of the others: the print store refuses a <c>.bbf</c>; a firmware image reaches a printer only
/// through the firmware flow, which sends nothing it has not found intact and signed by Prusa; and
/// <see cref="GcodeAllowList"/> admits <c>M997</c> only as <see cref="FlashFirmware.FlashLine"/>, the
/// one path that flow writes to. Making this class sendable would make the allow list the only
/// barrier left, so it must not then be widened.
/// </para>
/// </remarks>
public class GCode : ICommand
{
    public required byte[] GCodeData { get; set; }

    public uint Size { get; set; }
}
