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
    Undefined = 0,

    /// <summary>The Connect WebSocket, which is what modern firmware opens.</summary>
    WebSocket = 1,

    /// <summary>The pre-websocket transport: telemetry and events posted, commands collected in reply.</summary>
    Http = 2,
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
/// What it called itself, or <see langword="null"/> where it said nothing - which is Buddy.
/// </param>
/// <param name="FirmwareVersion">
/// The version it reported in <c>INFO</c>, or <see langword="null"/> before one has arrived.
/// </param>
public sealed record PrinterClient(PrinterTransport Transport, string? UserAgent, string? FirmwareVersion = null)
{
    /// <summary>A connection whose client has not announced itself, on the given transport.</summary>
    public static PrinterClient Anonymous(PrinterTransport transport)
    {
        return new PrinterClient(transport, null);
    }

    /// <summary>
    /// The product token the Python Connect SDK puts at the front of its user agent.
    /// </summary>
    /// <remarks>
    /// Its own, from <c>make_headers</c>: <c>f"Prusa-Connect-SDK-Printer/{__version__}"</c>. The
    /// version follows the slash and is deliberately not matched - a client is recognised by what it
    /// is, not by which release it is on.
    /// </remarks>
    public const string ConnectSdkProduct = "Prusa-Connect-SDK-Printer";

    /// <summary>
    /// Whether this is the Python Connect SDK, recognised by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Positive identification, not "said something".</b> An earlier version treated any non-empty
    /// user agent as the SDK, which is the wrong way round: the SDK is the client we have measured
    /// and can name, and everything else should keep the path this transport was built for. Getting
    /// that backwards is not a cosmetic error - a Buddy printer handed the SDK's download would be
    /// sent a command whose inline chunk request has no URL, and firmware asserts on it.
    /// </para>
    /// <para>
    /// <b>So an unrecognised agent is Buddy</b>, which is also the safe answer if anything upstream
    /// ever inserts one. Today nothing does: the proxy in front of the printer listener sets Host,
    /// X-Real-IP and X-Forwarded-Proto and passes the client's own agent through untouched.
    /// </para>
    /// </remarks>
    public bool IsConnectSdk =>
        UserAgent?.StartsWith(ConnectSdkProduct, System.StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether this client announced something we do not recognise - worth saying out loud, because
    /// it is treated as Buddy and that assumption is the one most likely to be wrong.
    /// </summary>
    public bool IsUnrecognised => UserAgent is not null && !IsConnectSdk;

    /// <summary>What to put in a log line, without a null reading as a missing value.</summary>
    public string Describe
    {
        get
        {
            string who = UserAgent ?? "unannounced (Buddy)";

            return FirmwareVersion is null ? $"{Transport}, {who}" : $"{Transport}, {who}, firmware {FirmwareVersion}";
        }
    }

    /// <summary>
    /// The same client, with the version it has just reported.
    /// </summary>
    /// <remarks>
    /// <b>Learned rather than announced, and only once <c>INFO</c> has arrived</b> - Buddy sends no
    /// user agent, so the version is the only thing that distinguishes one Buddy from another, and it
    /// does not turn up until the printer describes itself. Anything reading this must therefore cope
    /// with not knowing yet, which is why it is nullable rather than defaulted to something plausible.
    /// </remarks>
    public PrinterClient WithFirmware(string? firmwareVersion)
    {
        return firmwareVersion is null || firmwareVersion == FirmwareVersion
            ? this
            : this with { FirmwareVersion = firmwareVersion };
    }
}
