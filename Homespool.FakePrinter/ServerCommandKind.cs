namespace Homespool.FakePrinter;

/// <summary>
/// The five command-type characters a server may prefix a WebSocket frame with
/// (Prusa-Firmware-Buddy <c>connect.cpp:479-521</c>, <c>receive_command</c>'s switch on
/// <c>buffer[0]</c>, at the pinned ref). Values are the wire characters themselves.
/// </summary>
public enum ServerCommandKind
{
    Undefined = 0,

    /// <summary><c>J</c> - a JSON command, <c>{"command": "..."}</c>.</summary>
    Json = 'J',

    /// <summary><c>G</c> - GCode, processed as a background command.</summary>
    Gcode = 'G',

    /// <summary><c>F</c> - forced GCode. The firmware treats it identically to <c>G</c> today.</summary>
    ForcedGcode = 'F',

    /// <summary><c>D</c> - debug message. The firmware logs it and throws it away.</summary>
    Debug = 'D',

    /// <summary><c>T</c> - an inline-transfer chunk, routed to the transfer engine.</summary>
    TransferChunk = 'T',
}
