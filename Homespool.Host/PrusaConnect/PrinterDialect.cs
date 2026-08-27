namespace Homespool.Host.PrusaConnect;

/// <summary>
/// Which variant of the Connect protocol the thing at the other end actually speaks.
/// </summary>
/// <remarks>
/// <para>
/// <b>One named thing rather than a predicate per question.</b> Three clients speak this protocol and
/// they differ in ways that are not independent: firmware on a socket takes the inline transfer, firmware without one fetches an encrypted download it decrypts itself, and the Python
/// SDK fetches a plain one because it has no decryption at all. Asking those as separate booleans
/// spreads the same three-way choice across every caller, and each new question adds a member to the
/// connection abstraction - which is how <c>CanStreamChunks</c> and "can it decrypt" ended up as
/// neighbours describing one fact.
/// </para>
/// <para>
/// <b><c>START_CONNECT_DOWNLOAD</c> means two different things, and that is why this type exists.</b>
/// To the Python SDK it is "fetch <c>/p/teams/{team_id}/files/{hash}/raw</c>". To Buddy from v6.2.6 it
/// is a plain alias for <c>StartInlineDownload</c> - same handler, same four arguments - so it starts
/// an <i>inline</i> transfer over the socket. One wire name, one payload, two behaviours chosen by
/// what is reading it.
/// </para>
/// <para>
/// <b>So the two cannot be collapsed into one path, and sending the wrong one is not a soft
/// failure.</b> A websocket-less Buddy handed <c>START_CONNECT_DOWNLOAD</c> would try to open an
/// inline transfer over a socket it does not have, which is the <c>assert(0)</c> annotated <i>"Not
/// used in non-websocket mode"</i>. The plain team-URL download it once had was removed in v6.0.0
/// (<i>Rip out parsing of plain downloads</i>, 2023-10-10) and has never come back; only the SDK
/// still implements it.
/// </para>
/// <para>
/// <b>Which makes the conservative default load-bearing rather than tidy.</b> Anything unrecognised
/// is treated as Buddy and sent the encrypted download, because that is the one command every client
/// on this transport has understood in every release since Connect existed.
/// </para>
/// <para>
/// <b>This is a dialect, not a protocol.</b> All three are Prusa Connect and share its vocabulary,
/// its endpoints and its command names; what differs is which subset each implements. A second
/// <i>protocol</i> - Moonraker, Bambu - is a different module entirely and does not belong here.
/// </para>
/// <para>
/// <b>Derived from the connection, never stored on the printer.</b> The same machine changes dialect
/// when its firmware is rebuilt with websockets on or off, so this is a property of how it is talking
/// to us right now.
/// </para>
/// </remarks>
/// <param name="Name">What to call it in a log line.</param>
/// <param name="SupportsInlineTransfer">
/// Whether the inline transfer is available: the printer pulling a file in chunks over the same
/// connection its commands arrive on, which only a socket can carry.
/// </param>
/// <param name="UnderstandsEncryptedDownload">
/// Whether <c>START_ENCRYPTED_DOWNLOAD</c> is a command this client will answer. False for the Python
/// SDK, which has no such command and replies to it with nothing at all - measured 2026-08-18.
/// </param>
public sealed record PrinterDialect(string Name, bool SupportsInlineTransfer, bool UnderstandsEncryptedDownload)
{
    /// <summary>
    /// Buddy on the Connect WebSocket: the inline transfer, with no HTTP fetch involved. Named for
    /// Buddy rather than for the socket because a socket is no longer proof of Buddy - see
    /// <see cref="For"/>.
    /// </summary>
    public static readonly PrinterDialect BuddySocket = new("Buddy firmware over websocket", true, true);

    /// <summary>
    /// Buddy built without websockets: telemetry posted, commands collected in the reply, and a
    /// transfer fetched as AES-CTR ciphertext from the transfer listener.
    /// </summary>
    public static readonly PrinterDialect BuddyHttp = new("Buddy firmware over http", false, true);

    /// <summary>
    /// The Python Connect SDK, which is what an MK3S+ behind a Raspberry Pi runs. Same transport as
    /// <see cref="BuddyHttp"/>, and a different answer about transfers.
    /// </summary>
    public static readonly PrinterDialect ConnectSdk = new("Connect SDK over http", false, false);

    /// <summary>
    /// Works out the dialect from what the connection is and what the client said it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a client recognised by name gets the newer path.</b> The SDK is matched on its own
    /// product token; Buddy announces nothing at all - exactly <c>Fingerprint</c> and <c>Token</c>
    /// (<c>connect.cpp:137</c>) - and anything else, announced or silent, is treated as Buddy. That
    /// direction is the load-bearing one: a Buddy printer handed the SDK's download gets a command
    /// whose inline chunk request has no URL, which firmware asserts on.
    /// </para>
    /// <para>
    /// <b>Named for Buddy rather than for "firmware", because Prusa ships more than one.</b> The
    /// resin printers and the HT90 do not run this stack, and nobody here has seen what they send -
    /// so a dialect called <c>FirmwareHttp</c> would have been claiming knowledge about clients this
    /// project has never met. What was measured is Buddy's behaviour, and the name says only that.
    /// </para>
    /// </remarks>
    public static PrinterDialect For(PrinterClient? client, bool supportsInlineTransfer)
    {
        // The client is asked BEFORE the transport, and that order is the correction rather than a
        // style choice. There is a half-finished websocket branch on the SDK (c0a8936, "switch
        // telemetry and events transport from HTTP to WebSocket"), so "on a socket" will not stay a
        // synonym for "is Buddy" - and asking the transport first would classify such a client as
        // Buddy and offer it an inline transfer it cannot perform.
        //
        // NOT support for that branch, which is half finished and may land looking nothing like it
        // does today. It is only a refusal to assume: the ordering costs nothing and the assumption
        // would fail silently. What the branch shows is that the assumption is already shaky - it
        // leaves download.py untouched, so transfers still go through START_CONNECT_DOWNLOAD to the
        // team URL, and its socket sends JSON as TEXT frames while the inline transfer is binary.
        // If it ever ships, that is when to find out what it really does rather than guess now.
        if (client?.IsConnectSdk == true)
        {
            return ConnectSdk;
        }

        return supportsInlineTransfer ? BuddySocket : BuddyHttp;
    }
}
