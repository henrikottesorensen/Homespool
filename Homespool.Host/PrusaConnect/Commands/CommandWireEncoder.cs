using System;
using System.Text;
using System.Text.Json;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Encodes a <see cref="Command"/> for the wire - the body both transports share, and the
/// 9-byte-header frame the WebSocket command channel wraps it in (Prusa-Firmware-Buddy
/// connect.cpp:357-557 at the pinned ref).
/// </summary>
/// <remarks>
/// <b>The body is one thing and the frame is another</b>, and they are split here so the two
/// transports cannot drift on the half they share. The pre-websocket transport carries the same
/// body in an HTTP response, typed by <c>Content-Type</c> instead of by the frame's first byte
/// (<c>handle_server_resp</c>, connect.cpp:212-265 at v6.2.6). Whichever transport is asked, the
/// body goes through <see cref="EncodeBody"/> - which is where the gcode allowlist lives, and the
/// reason it must not be bypassed by a second encoder.
/// </remarks>
public static class CommandWireEncoder
{
    private const int HeaderLength = 9;

    /// <summary>The <c>Content-Type</c> firmware parses as a JSON command.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>The <c>Content-Type</c> firmware parses as a gcode command.</summary>
    public const string GcodeContentType = "text/x.gcode";

    /// <summary>
    /// A command's body and how firmware should read it: <see cref="ContentType"/> is the HTTP
    /// transport's spelling of what the frame's first byte says on the socket.
    /// </summary>
    /// <param name="Payload">The bytes: a JSON document, or a gcode line.</param>
    /// <param name="ContentType">One of <see cref="JsonContentType"/> or <see cref="GcodeContentType"/>.</param>
    public sealed record Body(byte[] Payload, string ContentType);

    /// <summary>
    /// The transport-independent body of a command.
    /// </summary>
    /// <remarks>
    /// Two shapes, both confirmed against firmware's own parser tests. A NO_ARGS command's body is
    /// just <c>{"command": "..."}</c> with no wrapper (command.cpp:149-166) - the shape verified
    /// against the live MK3.5, so it is kept byte-identical rather than folded into the general
    /// case. A command with kwargs carries an empty <c>args</c> array alongside them, which is how
    /// every argument-bearing case in tests/unit/connect/command.cpp is written.
    /// </remarks>
    /// <exception cref="ArgumentException">A gcode line not on <see cref="GcodeAllowList"/>.</exception>
    public static Body EncodeBody(ISendableCommand commandData)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        // A gcode command is a different body entirely: the line itself rather than a JSON document.
        if (commandData is ISendableGcodeCommand gcodeCommand)
        {
            return new Body(EncodeGcodeLine(gcodeCommand), GcodeContentType);
        }

        byte[] payload = commandData.Arguments is null ?
            JsonSerializer.SerializeToUtf8Bytes(new { command = commandData.WireName }) :
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                command = commandData.WireName,
                args = System.Array.Empty<object>(),
                kwargs = commandData.Arguments,
            });

        return new Body(payload, JsonContentType);
    }

    /// <summary>
    /// The WebSocket frame: the 9-byte header - <c>J</c> or <c>G</c>, then the command id as eight
    /// hex digits - followed by the body.
    /// </summary>
    public static byte[] Encode(uint commandId, ISendableCommand commandData)
    {
        Body body = EncodeBody(commandData);
        byte[] frame = new byte[HeaderLength + body.Payload.Length];

        // F/D/T remain out of scope.
        frame[0] = (byte)(body.ContentType == GcodeContentType ? 'G' : 'J');
        Encoding.ASCII.GetBytes(commandId.ToString("X8"), 0, 8, frame, 1);
        body.Payload.CopyTo(frame, HeaderLength);

        return frame;
    }

    /// <summary>
    /// The gcode line as bytes, after the allowlist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The allowlist is enforced here rather than only at the caller</b>, so it cannot be skipped
    /// by a new call path. This is the last point at which anything is still a refusable object; past
    /// it the line is bytes on the wire. Firmware's <c>M997</c> reflashes the mainboard from a file
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
    private static byte[] EncodeGcodeLine(ISendableGcodeCommand commandData)
    {
        if (!GcodeAllowList.IsAllowed(commandData.Line))
        {
            throw new ArgumentException(
                $"'{commandData.Line}' is not a gcode line this application may send.",
                nameof(commandData));
        }

        return Encoding.ASCII.GetBytes(commandData.Line);
    }
}
