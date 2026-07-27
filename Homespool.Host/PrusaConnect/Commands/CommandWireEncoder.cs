using System.Text;
using System.Text.Json;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Encodes a <see cref="Command"/> into the 9-byte-header-plus-JSON frame the firmware expects on
/// the WebSocket command channel (Prusa-Firmware-Buddy connect.cpp:357-557 at the pinned ref).
/// </summary>
public static class CommandWireEncoder
{
    private const int HeaderLength = 9;

    public static byte[] Encode(uint commandId, ISendableCommand commandData)
    {
        // Two shapes, both confirmed against firmware's own parser tests. A NO_ARGS command's body
        // is just {"command": "..."} with no wrapper (command.cpp:149-166) - the shape verified
        // against the live MK3.5, so it is kept byte-identical rather than folded into the general
        // case. A command with kwargs carries an empty "args" array alongside them, which is how
        // every argument-bearing case in tests/unit/connect/command.cpp is written.
        byte[] payload = commandData.Arguments is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { command = commandData.WireName })
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                command = commandData.WireName,
                args = System.Array.Empty<object>(),
                kwargs = commandData.Arguments,
            });
        byte[] frame = new byte[HeaderLength + payload.Length];

        frame[0] = (byte)'J'; // only J is modeled by ISendableCommand this pass; G/F/D/T are out of scope.
        Encoding.ASCII.GetBytes(commandId.ToString("X8"), 0, 8, frame, 1);
        payload.CopyTo(frame, HeaderLength);

        return frame;
    }
}
