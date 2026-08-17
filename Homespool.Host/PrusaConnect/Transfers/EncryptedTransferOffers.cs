using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// The keys behind encrypted downloads: for each transfer a printer will fetch as
/// <c>/f/&lt;iv&gt;/raw</c>, the AES key it was told and the offer token the bytes are pinned under.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside <see cref="TransferOfferStore"/>, not inside it.</b> That store pins bytes under an
/// opaque token and knows nothing of ciphers, which is right: the inline path uses it with no key at
/// all. This holds only what the encrypted path adds - the key, and which offer it belongs to - and
/// keys the two together by the IV, because the IV is what the printer's request carries.
/// </para>
/// <para>
/// <b>The IV is the capability.</b> The request that fetches a file presents no fingerprint and no
/// token (download.cpp:199 at v6.2.6 - <c>GET</c>, <c>Host</c>, <c>Content-Encryption-Mode</c>,
/// <c>Range</c>, nothing else). Whoever knows the IV can fetch the ciphertext; whoever also knows the
/// key can read it. Both are 128 random bits minted per transfer and travel to the printer only inside
/// the command, over the Connect channel. Guessing the IV is not the exposure worth reasoning about;
/// see <see cref="TransferCipher"/>'s remarks for the one that is, which is malleability.
/// </para>
/// <para>
/// <b>Lives exactly as long as the offer.</b> Registered when the offer is made and the command sent,
/// removed - and the key zeroed - when the offer is revoked or the transfer ends. A key that outlived
/// its bytes would be a secret kept for nothing.
/// </para>
/// </remarks>
public sealed class EncryptedTransferOffers
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a transfer: the printer will ask for <paramref name="ivHex"/>, the bytes are pinned
    /// under <paramref name="offerToken"/>, and <paramref name="key"/> is what it was told.
    /// </summary>
    /// <remarks>The key is copied; the caller may zero its own afterwards.</remarks>
    public void Register(string ivHex, ReadOnlySpan<byte> key, string offerToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ivHex);
        ArgumentException.ThrowIfNullOrEmpty(offerToken);

        if (key.Length != TransferCipher.KeyLength)
        {
            throw new ArgumentException($"The key must be exactly {TransferCipher.KeyLength} bytes.", nameof(key));
        }

        Entry entry = new(key.ToArray(), offerToken);

        if (_entries.TryRemove(ivHex, out Entry? replaced))
        {
            // Two transfers under one IV would share a keystream, which TransferCipher's remarks
            // call the one fatal shortcut. IVs are random per transfer, so this is theoretical - but
            // if it ever happens the old key must not linger.
            replaced.Zero();
        }

        _entries[ivHex] = entry;
    }

    /// <summary>
    /// The key and offer token behind an IV, for the request that presents it. Null when nothing was
    /// registered under it - or when it has since been revoked, which to a fetching printer is the
    /// same 404.
    /// </summary>
    public EncryptedTransfer? Find(string ivHex)
    {
        return _entries.TryGetValue(ivHex, out Entry? entry) ? new EncryptedTransfer(entry.Key, entry.OfferToken) : null;
    }

    /// <summary>Forgets an IV and zeroes its key. Idempotent.</summary>
    public void Revoke(string ivHex)
    {
        if (_entries.TryRemove(ivHex, out Entry? entry))
        {
            entry.Zero();
        }
    }

    private sealed class Entry
    {
        public Entry(byte[] key, string offerToken)
        {
            Key = key;
            OfferToken = offerToken;
        }

        public byte[] Key { get; }

        public string OfferToken { get; }

        public void Zero()
        {
            CryptographicOperations.ZeroMemory(Key);
        }
    }
}

/// <summary>What a printer's <c>/f/&lt;iv&gt;/raw</c> request resolves to: the key it was told, and
/// the offer holding the bytes.</summary>
/// <param name="Key">The AES-128 key. Not a copy - do not retain it past the request.</param>
/// <param name="OfferToken">The <see cref="ITransferContentStore"/> token to open the bytes under.</param>
public sealed record EncryptedTransfer(byte[] Key, string OfferToken);
