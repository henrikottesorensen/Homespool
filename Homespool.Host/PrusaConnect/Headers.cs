namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Header names of the Prusa Connect printer protocol, spelled as firmware sends and reads them.
/// Nothing else belongs here: general HTTP names are in <c>HeaderNames</c>, ours in
/// <see cref="Http.CustomHeaderNames"/>.
/// </summary>
public static class Headers
{
    /// <summary>Registration response header carrying the code the printer shows to its user. Firmware reads <c>Code</c> and <c>Temporary-Code</c> alike; on a poll the printer sends it back so the server can find the pending registration.</summary>
    public const string Code = nameof(Code);

    /// <summary>
    /// On the pre-websocket HTTP transport, the id of the command carried in a telemetry response.
    /// Firmware's response parser is generated from a table that spells it exactly this way
    /// (<c>utils/gen-automata/http_client.py</c>), and reads the value as base-ten digits.
    /// </summary>
    public const string CommandId = "Command-Id";

    /// <summary>Registration response header: when the registration code stops being valid, in RFC 1123 form.</summary>
    public const string Expires = nameof(Expires);

    /// <summary>Request header carrying the printer's fingerprint, which identifies it on every request.</summary>
    public const string Fingerprint = nameof(Fingerprint);

    /// <summary>Request header the printer sends naming the server it was configured for.</summary>
    public const string Host = nameof(Host);

    /// <summary>Request header on the websocket upgrade, naming the server the printer connected to.</summary>
    public const string Origin = nameof(Origin);

    /// <summary>Registration response header carrying the same code as <see cref="Code"/>, under the name the Python SDK reads.</summary>
    public const string TemporaryCode = "Temporary-Code";

    /// <summary>Request header carrying the printer's long-lived token, issued once a registration is claimed. Also the response header the claimed registration poll returns it in.</summary>
    public const string Token = nameof(Token);

    /// <summary>Request header naming the printer model.</summary>
    public const string UserAgentPrinter = "User-Agent-Printer";

    /// <summary>Request header carrying the printer's firmware version.</summary>
    public const string UserAgentVersion = "User-Agent-Version";

    /// <summary>Request header asking for the connection to become a websocket.</summary>
    public const string Upgrade = nameof(Upgrade);

    /// <summary>Headers of the websocket handshake a printer performs.</summary>
    public static class WebSocket
    {
        /// <summary>Server's answer to <see cref="Key"/>, completing the websocket handshake.</summary>
        public const string Accept = "Sec-WebSocket-Accept";

        /// <summary>Negotiated websocket extensions.</summary>
        public const string Extensions = "Sec-WebSocket-Extensions";

        /// <summary>Client's random handshake nonce.</summary>
        public const string Key = "Sec-WebSocket-Key";

        /// <summary>Subprotocol the client asks for, and the server confirms.</summary>
        public const string Protocol = "Sec-WebSocket-Protocol";

        /// <summary>Websocket protocol version; always 13.</summary>
        public const string Version = "Sec-WebSocket-Version";
    }

    /// <summary>Header values that are part of the protocol.</summary>
    public static class Values
    {
        /// <summary>The websocket subprotocol a printer asks for in <see cref="WebSocket.Protocol"/>.</summary>
        public const string WSProtocolPrusaConnect = "prusa-connect";
    }
}
