using System;
using System.Net.Mime;
using System.Text;
using System.Text.Encodings.Web;
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

    /// <summary>
    /// The encoding for a peer that does <b>not</b> decode <c>\uXXXX</c>, which is every client this
    /// project has measured - so it is also the default when nothing is known about the peer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Unsafe" here means "unsafe to embed in HTML", and nothing else.</b> The name warns that the
    /// output leaves <c>&lt; &gt; &amp; ' +</c> unescaped, so dropping it into a <c>&lt;script&gt;</c>
    /// block or an HTML attribute would let a value close the element it sits in. That is a real
    /// hazard and it is not this one: a <see cref="Body"/> is written to a websocket frame
    /// (<c>WebSocketPrinterConnection.SendCommandAsync</c>) or returned as an HTTP response body
    /// (<c>PrusaConnectPrinterController</c>), read by a printer, and never rendered by anything. The
    /// output is ordinary, conformant JSON; only its escape set is wider.
    /// </para>
    /// <para>
    /// <b>Why it is needed rather than merely tidier.</b> Firmware's <c>unescape_json_i</c>
    /// (<c>json_encode.cpp:21-29</c>) decodes only <c>\b \f \n \r \t \" \\</c> - <b>not</b>
    /// <c>\uXXXX</c>, which falls through to <c>if (!escaped) *write++ = *read++</c> and keeps its
    /// backslash. FatFs then treats that backslash as a path separator (<c>ff.c:51</c>), so
    /// <c>Le t\u00E9st\u00E8 \u0026 st\u00FCff.bgcode</c> arrives as six path segments instead of one
    /// and <c>Transfer::begin</c>'s <c>mkdir</c> is aimed at a parent that does not exist. The printer
    /// answers <i>"Failed to create directory"</i>, accurately, and retries for ever. Measured on a
    /// Core One at <c>6.8.1+16182</c>.
    /// </para>
    /// <para>
    /// <b>The obvious narrower choice does not work, which is worth recording because it looks like it
    /// should.</b> <c>JavaScriptEncoder.Create(UnicodeRanges.All)</c> stops escaping non-ASCII but
    /// still emits <c>\u0026</c>, <c>\u002B</c> and <c>\u0027</c> for <c>&amp; + '</c> - so it fixes
    /// the accents in that filename and leaves the <c>&amp;</c> to split the path exactly as before.
    /// Measured, not assumed. Of the stock encoders only this one escapes a set firmware can actually
    /// read back.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions UnescapedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The stock encoding, for a peer known to decode <c>\uXXXX</c> - which is nothing today, and
    /// quite possibly nothing ever.
    /// </summary>
    /// <remarks>
    /// Reached only when <see cref="PrinterDialect.DecodesJsonUnicodeEscapes"/> is true, which no
    /// dialect sets: the firmware fix is an unmerged outside PR against a project that rarely takes
    /// them. Kept because the alternative is that a future patched printer has nowhere to be
    /// described, and because a branch nothing takes is cheaper than one nobody can add later - but
    /// read <see cref="UnescapedOptions"/> as the encoding this application uses, and this one as the
    /// exception that may never occur.
    /// </remarks>
    private static readonly JsonSerializerOptions EscapedOptions = new();

    /// <summary>The <c>Content-Type</c> firmware parses as a JSON command.</summary>
    public const string JsonContentType = MediaTypeNames.Application.Json;

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
    /// <param name="commandData">The command to put on the wire.</param>
    /// <param name="dialect">
    /// What the peer can read back, or <see langword="null"/> where that is not known yet - see
    /// <see cref="UnescapedOptions"/> for why not-known takes the wider escape set.
    /// </param>
    /// <exception cref="ArgumentException">A gcode line not on <see cref="GcodeAllowList"/>.</exception>
    public static Body EncodeBody(ISendableCommand commandData, PrinterDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        // A gcode command is a different body entirely: the line itself rather than a JSON document.
        if (commandData is ISendableGcodeCommand gcodeCommand)
        {
            return new Body(EncodeGcodeLine(gcodeCommand), GcodeContentType);
        }

        // Defaulting to the unescaped form rather than to the stock encoder is the conservative
        // direction, not the lax one: its output is valid JSON that every conformant parser reads,
        // and it is additionally the only form the firmware in the field reads correctly. A caller
        // that has not said what the peer is therefore gets the encoding that works for all of them,
        // in the same spirit as PrinterDialect treating an unrecognised client as Buddy.
        JsonSerializerOptions options = dialect?.DecodesJsonUnicodeEscapes == true ? EscapedOptions : UnescapedOptions;

        byte[] payload = commandData.Arguments is null ?
            JsonSerializer.SerializeToUtf8Bytes(new { command = commandData.WireName }, options) :
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    command = commandData.WireName,
                    args = System.Array.Empty<object>(),
                    kwargs = commandData.Arguments,
                },
                options);

        return new Body(payload, JsonContentType);
    }

    /// <summary>
    /// The WebSocket frame: the 9-byte header - <c>J</c> or <c>G</c>, then the command id as eight
    /// hex digits - followed by the body.
    /// </summary>
    public static byte[] Encode(uint commandId, ISendableCommand commandData, PrinterDialect? dialect = null)
    {
        Body body = EncodeBody(commandData, dialect);
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
