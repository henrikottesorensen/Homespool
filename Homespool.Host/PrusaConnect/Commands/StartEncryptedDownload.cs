using System;
using System.Collections.Generic;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Tells the printer to fetch a file over <b>a plain HTTP connection it opens itself</b>, decrypting
/// with a key and IV we chose - the <c>Encrypted</c>/<c>Async</c> engine, rather than the byte-range
/// requests over the Connect WebSocket that <see cref="StartConnectDownload"/> provokes.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>PrintFileSender</c> sends this</b> to a printer that cannot pull chunks over a Connect
/// WebSocket but does understand the command - Buddy on the HTTP transport. It is the second choice
/// of two: the inline path is ~13% faster at every size measured, so a printer that can stream chunks
/// gets <see cref="StartConnectDownload"/> instead.
/// </para>
/// <para>
/// <b>Nothing in the wild provoked it, and no capture corroborates it.</b> Buddy answers a
/// Connect-initiated print upload inline every time (command.cpp:186-196 at the pinned ref), so
/// everything here was read from firmware source rather than observed - then confirmed against an
/// MK3.5, which accepted this exact shape, fetched the file, and asked for the URL below character
/// for character as predicted.
/// </para>
/// <para>
/// <b>The request URL is derived from the IV alone</b> - <c>/f/&lt;iv-as-lowercase-hex&gt;/raw</c>
/// (<c>make_enc_url</c>, planner.cpp:191-206). No path, no hash, no transfer id: the IV is at once
/// the CTR nonce, the capability token and the file handle. <see cref="UrlPath"/> is that URL, so a
/// caller can register the offer under the same value the printer will ask for.
/// </para>
/// <para>
/// <b><see cref="Port"/> is effectively required, despite being optional on the wire.</b> Firmware
/// resolves the port from the printer's own enrolled config and silently rewrites 443-with-TLS to 80
/// (<c>host_and_port</c>, planner.cpp:176-189). A deployment whose printers are told 443 - the
/// default - would therefore aim a plain GET at whatever answers on port 80, so the override is the
/// only way to name a port we actually serve. The <b>host</b> is never ours to choose here: it is
/// always the server the printer is currently enrolled against, which makes it a property of the
/// last provisioning bundle rather than of this command.
/// </para>
/// </remarks>
public class StartEncryptedDownload : ISendableCommand
{
    /// <summary>Firmware parses both into a 16-byte <c>Block</c> (command.hpp:54-66).</summary>
    public const int KeyLength = 16;

    /// <inheritdoc cref="KeyLength"/>
    public const int IvLength = 16;

    /// <summary>
    /// Where the file lands on the printer. Must sit under <c>/usb</c>, carry a transferrable
    /// extension and contain no <c>/../</c>, or the command is refused outright (<c>path_allowed</c>,
    /// planner.cpp:135-141).
    /// </summary>
    public required string Path { get; set; }

    /// <summary>The AES-128-CTR key. Never travels over the HTTP connection - only over this one.</summary>
    public required byte[] Key { get; set; }

    /// <summary>
    /// The AES-128-CTR nonce, which is also the URL the printer will request. Must be freshly random
    /// per transfer: see <see cref="Transfers.TransferCipher"/> for why deriving it from the file is
    /// the one shortcut that must never be taken.
    /// </summary>
    public required byte[] Iv { get; set; }

    /// <summary>
    /// Plaintext length. <c>uint32</c> on the wire, so this cannot describe a file above 4 GiB - a
    /// FatFS limit firmware's own comment acknowledges (command.hpp:64).
    /// </summary>
    public long OriginalSize { get; set; }

    /// <summary>
    /// The port to fetch from, overriding what firmware would otherwise derive. Omitted only when a
    /// deployment genuinely serves this on the port its printers were enrolled with.
    /// </summary>
    public ushort? Port { get; set; }

    /// <summary>The name this command goes out under. See <see cref="StartConnectDownload.Wire"/>.</summary>
    public const string Wire = "START_ENCRYPTED_DOWNLOAD";

    public string WireName => Wire;

    /// <summary>
    /// The path the printer will GET, derived from <see cref="Iv"/> exactly as firmware derives it.
    /// </summary>
    public string UrlPath => $"/f/{Convert.ToHexStringLower(Iv)}/raw";

    /// <summary>
    /// <c>ARGS_ENC_DOWN</c> (command.cpp:87) is path, key, iv and orig_size - all four required, any
    /// one missing making the whole command a <c>BrokenCommand</c>. <c>port</c> carries no such flag
    /// and is genuinely optional, so it is omitted rather than sent as null when unset.
    /// </summary>
    /// <remarks>
    /// Key and IV are hex strings of exactly 32 characters; firmware refuses any other length
    /// outright (<c>decode_hex</c>, command.cpp:57-68). It parses them with <c>strtoul</c> base 16,
    /// so case does not matter - lowercase is used because that is what firmware itself emits when it
    /// builds the URL from the same bytes, and having the two agree makes the join visible.
    /// </remarks>
    public IReadOnlyDictionary<string, object?> Arguments
    {
        get
        {
            Dictionary<string, object?> arguments = new()
            {
                ["path"] = Path,
                ["key"] = Convert.ToHexStringLower(Key),
                ["iv"] = Convert.ToHexStringLower(Iv),
                ["orig_size"] = OriginalSize,
            };

            if (Port is ushort port)
            {
                arguments["port"] = port;
            }

            return arguments;
        }
    }
}
