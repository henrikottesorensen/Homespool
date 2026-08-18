namespace Homespool.Host.PrusaConnect;

/// <summary>How a printer is talking to us.</summary>
/// <remarks>
/// A property of the connection, never of the printer: the same machine moves between these when its
/// firmware is rebuilt with websockets on or off, so persisting it on a row would freeze a fact that
/// moves - the shape of mistake <c>notes/tls-by-default.md</c> records about the certificate issued
/// at first run.
/// </remarks>
public enum PrinterTransport
{
    /// <summary>Reserved, so a default-constructed value is not a claim about anything.</summary>
    Unknown = 0,

    /// <summary>The Connect WebSocket, which is what modern firmware opens.</summary>
    WebSocket,

    /// <summary>The pre-websocket transport: telemetry and events posted, commands collected in reply.</summary>
    Http,
}

/// <summary>
/// What is at the other end of a connection, as it announced itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>An observation, not a conclusion.</b> What this is used for today is one question - whether an
/// encrypted download is a command this client will answer - but recording the answer alone would
/// throw away the evidence, and the next question would have to re-plumb the whole path to ask it.
/// The version is here for that reason rather than because anything reads it yet: an SDK release that
/// gains a command is a version comparison, and a client that cannot be identified at all is a fact
/// worth being able to state.
/// </para>
/// <para>
/// <b>It also answers something Homespool could not say before</b> (<c>notes/diagnostics.md</c>, the
/// blind spots): a printer that behaves oddly could not be asked what it was running. The connect log
/// line now carries it.
/// </para>
/// <para>
/// <b>Firmware announces nothing</b>, and that is the discriminator rather than an accident - read at
/// source 2026-08-18: Buddy sends exactly <c>Fingerprint</c> and <c>Token</c> on the transport
/// (<c>connect.cpp:137</c>) and no user agent of any kind, where the Python SDK sends
/// <c>Prusa-Connect-SDK-Printer/&lt;version&gt;</c> on every request. So a null agent means firmware,
/// which is the conservative way round: an unidentified client is treated as the one this transport
/// was built for.
/// </para>
/// </remarks>
/// <param name="Transport">Which way in it came.</param>
/// <param name="UserAgent">
/// What it called itself, or <see langword="null"/> where it said nothing - which is firmware.
/// </param>
public sealed record PrinterClient(PrinterTransport Transport, string? UserAgent)
{
    /// <summary>A connection whose client has not announced itself, on the given transport.</summary>
    public static PrinterClient Anonymous(PrinterTransport transport)
    {
        return new PrinterClient(transport, null);
    }

    /// <summary>
    /// Whether this client understands <c>START_ENCRYPTED_DOWNLOAD</c>.
    /// </summary>
    /// <remarks>
    /// The AES-CTR download is a Buddy feature (<c>notes/encrypted-download.md</c>); the Python SDK
    /// has no such command and answers nothing at all, so an encrypted offer to one is a send that
    /// times out. Measured against the real SDK 2026-08-18.
    /// </remarks>
    public bool UnderstandsEncryptedDownload => UserAgent is null;

    /// <summary>What to put in a log line, without a null reading as a missing value.</summary>
    public string Describe => UserAgent is null ? $"{Transport}, unannounced (firmware)" : $"{Transport}, {UserAgent}";
}
