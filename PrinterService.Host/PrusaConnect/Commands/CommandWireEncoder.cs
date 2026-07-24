using System.Text;
using System.Text.Json;

namespace PrinterService.Host.PrusaConnect.Commands;

/// <summary>
/// Encodes a <see cref="Command"/> into the 9-byte-header-plus-JSON frame the firmware expects on
/// the WebSocket command channel (Prusa-Firmware-Buddy connect.cpp:357-557 at the pinned ref).
/// </summary>
public static class CommandWireEncoder
{
    private const int HeaderLength = 9;

    public static byte[] Encode(uint commandId, ISendableCommand commandData)
    {
        // Confirmed against command.cpp:149-166: a NO_ARGS command's JSON body is just
        // {"command": "..."} - no "args"/"kwargs" wrapper. Every ISendableCommand so far is
        // NO_ARGS. If a future command needs kwargs, this is the one place to extend.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { command = commandData.WireName });
        byte[] frame = new byte[HeaderLength + payload.Length];

        frame[0] = (byte)'J'; // only J is modeled by ISendableCommand this pass; G/F/D/T are out of scope.
        Encoding.ASCII.GetBytes(commandId.ToString("X8"), 0, 8, frame, 1);
        payload.CopyTo(frame, HeaderLength);

        return frame;
    }
}
