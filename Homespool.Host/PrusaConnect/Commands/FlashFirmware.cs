using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Reflashes the printer from the firmware image at <see cref="DrivePath"/>: <c>M997</c> with that
/// path, which writes the image's short name where the bootloader looks and resets the board
/// (<c>src/marlin_stubs/M997.cpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One fixed line, because the path is parsed as parameters.</b> Firmware's gcode parser reads
/// every capital letter of the line as a parameter of its own, the path included, and <c>M997</c>
/// asks three of them: <c>S</c> chooses the module to flash - anything but zero and the printer does
/// nothing - <c>O</c> forces an older or equal version, and <c>B</c> is an address. A short name
/// carrying <c>S</c> and a digit would make a flash that silently never happens. <c>FIRMWARE.BBF</c>
/// holds none of the three outside its extension, is its own short name, and is the name the firmware
/// flow gives every image it sends.
/// </para>
/// <para>
/// <b>Never answered when it works</b>, like <see cref="ResetPrinter"/>: the board resets while the
/// reply would be on its way, so the flash is judged by the printer coming back with the image's
/// version, not by an answer to this.
/// </para>
/// </remarks>
public sealed class FlashFirmware : ISendableGcodeCommand
{
    /// <summary>The name every firmware image is sent to a printer under.</summary>
    public const string DriveName = "FIRMWARE.BBF";

    /// <summary>Where on the printer's drive that is.</summary>
    public const string DrivePath = "/usb/" + DriveName;

    /// <summary>The whole line, and the only <c>M997</c> line the allow list admits.</summary>
    public const string FlashLine = GcodeAllowList.ReflashCommand + " " + DrivePath;

    public string WireName => "GCODE";

    public string Line => FlashLine;

    /// <inheritdoc/>
    public bool ExpectsReply => false;

    /// <inheritdoc/>
    /// <remarks>Replacing what a printer runs is managing it, not operating it.</remarks>
    public Capability RequiredCapability => Capability.ManagePrinter;
}
