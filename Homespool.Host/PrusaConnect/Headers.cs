namespace Homespool.Host.PrusaConnect;

public static class Headers
{
    public const string Code = nameof(Code);

    /// <summary>
    /// On the pre-websocket HTTP transport, the id of the command carried in a telemetry response.
    /// Firmware's response parser is generated from a table that spells it exactly this way
    /// (<c>utils/gen-automata/http_client.py</c>), and reads the value as base-ten digits.
    /// </summary>
    public const string CommandId = "Command-Id";

    public const string Expires = nameof(Expires);

    public const string Fingerprint = nameof(Fingerprint);

    public const string Host = nameof(Host);

    public const string Origin = nameof(Origin);

    public const string TemporaryCode = "Temporary-Code";

    public const string Token = nameof(Token);

    public const string UserAgentPrinter = "User-Agent-Printer";

    public const string UserAgentVersion = "User-Agent-Version";

    public const string Upgrade = nameof(Upgrade);

    public static class WebSocket
    {
        public const string Accept = "Sec-WebSocket-Accept";

        public const string Extensions = "Sec-WebSocket-Extensions";

        public const string Key = "Sec-WebSocket-Key";

        public const string Protocol = "Sec-WebSocket-Protocol";

        public const string Version = "Sec-WebSocket-Version";
    }

    public static class Values
    {
        public const string WSProtocolPrusaConnect = "prusa-connect";
    }
}
