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
/// <b>These are not disjoint capability sets, and the names say what is CHOSEN rather than what is
/// supported.</b> Buddy is a superset: from 4.6.1, the first version with Connect at all, it carried
/// both the encrypted download and the SDK's team-URL method, and the SDK carries only the latter.
/// So <see cref="BuddyHttp"/> means "the encrypted download, which is what we send Buddy", not "the
/// only thing Buddy can do". Whether the team URL would serve both - collapsing this axis entirely -
/// is untested and recorded in <c>notes/http-transport.md</c>.
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
    /// <summary>Buddy on the Connect WebSocket: the inline transfer, with no HTTP fetch involved.</summary>
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
        if (supportsInlineTransfer)
        {
            return BuddySocket;
        }

        return client?.IsConnectSdk == true ? ConnectSdk : BuddyHttp;
    }
}
