namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Arbitrary gcode, sent to the printer to execute. A hollow marker today — it is not
/// <c>ISendableCommand</c>, so nothing can send it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read this before making it sendable.</b> Doing so is an obvious and reasonable feature, and it
/// silently completes a privilege-escalation chain whose other half lives somewhere nobody would
/// think to look — <c>UploadedFileStore.AllowedExtensions</c>.
/// </para>
/// <para>
/// Firmware's <c>M997</c> reflashes the mainboard from a <c>.bbf</c> image named by short filename
/// under <c>/usb/</c> (<c>src/marlin_stubs/M997.cpp</c>). The application validates <b>nothing</b>: it
/// writes the name into the bootloader handoff region and resets. Verification, if any, is the
/// bootloader's, and the bootloader is a separate binary — but firmware exposes a "is the running
/// firmware signed?" query (<c>support_utils.cpp</c>, <c>signature_exist</c>), which only makes sense
/// if unsigned images flash and are flagged rather than refused. Prusa support community builds, so
/// that is the expected posture rather than a defect.
/// </para>
/// <para>
/// So <b>upload a <c>.bbf</c></b> + <b>send <c>M997</c></b> = flash arbitrary firmware on someone's
/// printer. Both halves are currently blocked, independently and for unrelated reasons: the
/// allowlist refuses the upload, and this class cannot be sent. <b>Neither guard knows about the
/// other.</b> If gcode becomes sendable, the allowlist becomes the only barrier, and it must not then
/// be widened — see its own remarks.
/// </para>
/// <para>
/// If this is implemented, the safer shape is an allowlist of permitted gcodes rather than a
/// passthrough, and <c>M997</c> is the first entry that must not be on it.
/// </para>
/// </remarks>
public class GCode : ICommand
{
    public required byte[] GCodeData { get; set; }

    public uint Size { get; set; }
}
