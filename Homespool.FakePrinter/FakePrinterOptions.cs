using System;

namespace Homespool.FakePrinter;

/// <summary>
/// Knobs for one fake printer. Defaults are the firmware's own behaviour at the pinned ref; every
/// deviation a test sets is therefore explicit.
/// </summary>
public sealed class FakePrinterOptions
{
    /// <summary>
    /// The server's HTTP base address, e.g. <c>http://localhost:5052</c>. Used by
    /// <see cref="FakePrinterClient.RegisterAsync"/>'s default HTTP client and by the default
    /// WebSocket connector (scheme swapped to <c>ws</c>). Optional when both a caller-supplied
    /// <c>HttpClient</c> and a custom connector are used, as in-process tests do.
    /// </summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>
    /// Fragment size for outgoing messages. The firmware renders into a 512-byte buffer and sends
    /// each chunk as one WebSocket fragment (<c>MAX_RESP_SIZE</c>, connect.cpp:198 + 646-673), so
    /// real hardware fragments every message larger than 512 bytes.
    /// </summary>
    public int SendFragmentSize { get; init; } = 512;

    /// <summary>
    /// Keep-alive ping cadence. The firmware pings after 15 s without sending anything
    /// (<c>ping_inactivity</c>, connect.cpp:49-50). .NET cannot send explicit Ping frames, so this
    /// is approximated via the socket's keep-alive - a documented deviation
    /// (notes/fake-printer-harness.md, "Ping compromise").
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait for a Pong before giving up on the connection - matches the firmware's
    /// socket-level timeout (<c>SOCKET_TIMEOUT_SEC = 60</c>, connection_cache.cpp:18).
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>What the telemetry loop sends; null runs a command-answering connection only.</summary>
    public ITelemetrySource? TelemetrySource { get; init; }

    /// <summary>How commands are answered; null gets a <see cref="FirmwareFaithfulPolicy"/>.</summary>
    public CommandAnswerPolicy? Policy { get; init; }

    /// <summary>The <c>User-Agent-Printer</c> header value on registration requests.</summary>
    public string UserAgentPrinter { get; init; } = "MK3.5";
}
