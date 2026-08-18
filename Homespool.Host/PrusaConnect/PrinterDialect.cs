namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Which variant of the Connect protocol the thing at the other end actually speaks.
/// </summary>
/// <remarks>
/// <para>
/// <b>One named thing rather than a predicate per question.</b> Three clients speak this protocol and
/// they differ in ways that are not independent: firmware on a socket pulls a transfer in chunks over
/// that socket, firmware without one fetches an encrypted download it decrypts itself, and the Python
/// SDK fetches a plain one because it has no decryption at all. Asking those as separate booleans
/// spreads the same three-way choice across every caller, and each new question adds a member to the
/// connection abstraction - which is how <c>CanStreamChunks</c> and "can it decrypt" ended up as
/// neighbours describing one fact.
/// </para>
/// <para>
/// <b>This is a dialect, not a protocol.</b> All three are Prusa Connect and share its vocabulary,
/// its endpoints and its command names; what differs is which subset each implements. A second
/// <i>protocol</i> - Moonraker, Bambu - is a different module entirely and does not belong here, per
/// <c>notes/domain-vocabulary.md</c>.
/// </para>
/// <para>
/// <b>Derived from the connection, never stored on the printer.</b> The same machine changes dialect
/// when its firmware is rebuilt with websockets on or off, so this is a property of how it is talking
/// to us right now.
/// </para>
/// </remarks>
/// <param name="Name">What to call it in a log line.</param>
/// <param name="StreamsChunks">
/// Whether a transfer can be pulled over the connection itself, which only a socket can do.
/// </param>
/// <param name="UnderstandsEncryptedDownload">
/// Whether <c>START_ENCRYPTED_DOWNLOAD</c> is a command this client will answer. False for the Python
/// SDK, which has no such command and replies to it with nothing at all - measured 2026-08-18.
/// </param>
public sealed record PrinterDialect(string Name, bool StreamsChunks, bool UnderstandsEncryptedDownload)
{
    /// <summary>Firmware on the Connect WebSocket: chunks over the socket, no HTTP fetch involved.</summary>
    public static readonly PrinterDialect FirmwareSocket = new("firmware over websocket", true, true);

    /// <summary>
    /// Firmware built without websockets: telemetry posted, commands collected in the reply, and a
    /// transfer fetched as AES-CTR ciphertext from the transfer listener.
    /// </summary>
    public static readonly PrinterDialect FirmwareHttp = new("firmware over http", false, true);

    /// <summary>
    /// The Python Connect SDK, which is what an MK3S+ behind a Raspberry Pi runs. Same transport as
    /// <see cref="FirmwareHttp"/>, and a different answer about transfers.
    /// </summary>
    public static readonly PrinterDialect ConnectSdk = new("connect sdk over http", false, false);

    /// <summary>
    /// Works out the dialect from what the connection is and what the client said it was.
    /// </summary>
    /// <remarks>
    /// <b>The unidentified case is firmware, deliberately.</b> Buddy announces nothing at all - it
    /// sends exactly <c>Fingerprint</c> and <c>Token</c> (<c>connect.cpp:137</c>) - so silence is
    /// evidence rather than absence of it, and the only client that gets the newer path is one that
    /// names itself. A future client we have never heard of therefore behaves like the one this
    /// transport was built for.
    /// </remarks>
    public static PrinterDialect For(PrinterClient? client, bool streamsChunks)
    {
        if (streamsChunks)
        {
            return FirmwareSocket;
        }

        return client?.UserAgent is null ? FirmwareHttp : ConnectSdk;
    }
}
