using System;
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
        // A gcode command is a different frame entirely: type 'G', and the body is the line itself
        // rather than a JSON document. Same 9-byte header either way.
        if (commandData is ISendableGcodeCommand gcodeCommand)
        {
            return EncodeGcode(commandId, gcodeCommand);
        }

        // Two shapes, both confirmed against firmware's own parser tests. A NO_ARGS command's body
        // is just {"command": "..."} with no wrapper (command.cpp:149-166) - the shape verified
        // against the live MK3.5, so it is kept byte-identical rather than folded into the general
        // case. A command with kwargs carries an empty "args" array alongside them, which is how
        // every argument-bearing case in tests/unit/connect/command.cpp is written.
        byte[] payload = commandData.Arguments is null ?
            JsonSerializer.SerializeToUtf8Bytes(new { command = commandData.WireName }) :
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                command = commandData.WireName,
                args = System.Array.Empty<object>(),
                kwargs = commandData.Arguments,
            });
        byte[] frame = new byte[HeaderLength + payload.Length];

        frame[0] = (byte)'J'; // G is handled above; F/D/T remain out of scope.
        Encoding.ASCII.GetBytes(commandId.ToString("X8"), 0, 8, frame, 1);
        payload.CopyTo(frame, HeaderLength);

        return frame;
    }

    /// <summary>
    /// Encodes a <c>G</c> frame - the header, then the gcode line as bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The allowlist is enforced here rather than only at the caller</b>, so it cannot be skipped
    /// by a new call path. This is the last point at which anything is still a refusable object; past
    /// it the line is bytes on a socket. Firmware's <c>M997</c> reflashes the mainboard from a file
    /// on the USB stick and validates nothing (<see cref="GCode"/>), so "which lines may this
    /// application emit" is worth asking twice.
    /// </para>
    /// <para>
    /// Throwing rather than returning empty: a line reaching here that the allowlist refuses is a
    /// programming error, not a runtime condition, and silently sending nothing would be a printer
    /// that mysteriously ignores a command.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The line is not on <see cref="GcodeAllowList"/>.</exception>
    private static byte[] EncodeGcode(uint commandId, ISendableGcodeCommand commandData)
    {
        if (!GcodeAllowList.IsAllowed(commandData.Line))
        {
            throw new ArgumentException(
                $"'{commandData.Line}' is not a gcode line this application may send.",
                nameof(commandData));
        }

        byte[] payload = Encoding.ASCII.GetBytes(commandData.Line);
        byte[] frame = new byte[HeaderLength + payload.Length];

        frame[0] = (byte)'G';
        Encoding.ASCII.GetBytes(commandId.ToString("X8"), 0, 8, frame, 1);
        payload.CopyTo(frame, HeaderLength);

        return frame;
    }
}
