using System;
using System.Security.Cryptography;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// AES-128-CTR in the exact shape firmware's <c>transfers::Decryptor</c> expects (decrypt.cpp:19-48
/// at the pinned ref) - the cipher for the encrypted download, where the printer fetches a file over
/// a plain HTTP connection of its own rather than over the Connect WebSocket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing calls this, deliberately.</b> The encrypted download was built out, run against an
/// MK3.5 on 2026-07-31 and <b>rejected</b>: it is ~13% slower than the inline path at every size
/// measured, because the ceiling is the printer's write path rather than the transport
/// (<c>notes/encrypted-download.md</c>). Kept because this class and the keystream fixture beside it
/// are <i>evidence about firmware</i> rather than plumbing of ours - verified against Buddy's own
/// decryptor and against ciphertext Connect itself produced. Regenerating that costs a container
/// build and a firmware checkout; carrying it costs nothing. Delete both together, or neither.
/// </para>
/// <para>
/// <b>Confidentiality survives the plaintext transport, integrity does not.</b> The key reaches the
/// printer inside <c>START_ENCRYPTED_DOWNLOAD</c> over the TLS WebSocket and never crosses the HTTP
/// connection, so the bytes on that connection are ciphertext to anyone watching. Nothing
/// authenticates them, though: CTR is malleable, and firmware checks only status, length and the
/// <c>Content-Encryption-Mode</c> header (download.cpp:234-255) - there is no hash on this path at
/// all, unlike the inline one. An on-path attacker cannot read the gcode but <b>can corrupt it</b>,
/// bit for bit at a position of their choosing, and nothing at any layer will notice. That is gcode
/// driving heaters and steppers, so it is the reason this path is a LAN proposition and not an
/// internet-facing one; <c>notes/internet-exposure.md</c> is the other half of that argument.
/// </para>
/// <para>
/// <b>Never reuse a key and IV across two files.</b> CTR turns a repeated (key, nonce) pair into the
/// same keystream, so two files encrypted under one pair leak their XOR to anyone holding both -
/// which, for gcode sharing a slicer preamble, is most of the smaller file. Both values are the
/// server's to choose and the firmware neither knows nor cares, so this obligation is entirely ours:
/// <b>mint both at random, per transfer</b>.
/// </para>
/// <para>
/// The tempting shortcut is the fatal one, and it is worth naming because the protocol invites it.
/// The request URL <i>is</i> the IV in hex (<c>/f/&lt;iv&gt;/raw</c>, <c>make_enc_url</c>,
/// planner.cpp:191-206), so deriving the IV from the file's content hash would give stable, cacheable,
/// idempotent URLs - and would reuse one keystream for every transfer of that file, forever. A
/// content-addressed URL is not available here at any price worth paying.
/// </para>
/// <para>
/// <b>The counter is not the IV.</b> Firmware overwrites the IV's last
/// <see cref="CounterLength"/> bytes with the block index of the starting offset, big-endian, and
/// leaves the leading twelve alone. Encrypting a range at the wrong offset therefore produces
/// plausible-looking ciphertext that decrypts to garbage, which is the failure this class exists to
/// make impossible; <c>KeystreamFixtureTests</c> pins it against firmware's own code.
/// </para>
/// <para>
/// Symmetric, as any stream cipher is: <see cref="Transform"/> both encrypts and decrypts, and the
/// name says so rather than claiming a direction the maths does not have.
/// </para>
/// </remarks>
public sealed class TransferCipher : IDisposable
{
    /// <summary>AES block size, and the alignment every starting offset must satisfy.</summary>
    public const int BlockSize = 16;

    /// <summary>Key length. AES-128, fixed by the 16-byte <c>Block</c> firmware parses the hex into.</summary>
    public const int KeyLength = 16;

    /// <summary>IV length - the same <c>Block</c> type, and also what the URL carries as hex.</summary>
    public const int IvLength = 16;

    /// <summary>
    /// How many trailing IV bytes the starting block index replaces
    /// (<c>Decryptor::CtrCounterSize</c>, decrypt.hpp:16).
    /// </summary>
    public const int CounterLength = 4;

    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[BlockSize];
    private readonly byte[] _keystream = new byte[BlockSize];

    /// <summary>
    /// How much of <see cref="_keystream"/> is spent. Starts full so the first
    /// <see cref="Transform"/> generates a block rather than XOR-ing zeroes.
    /// </summary>
    private int _keystreamUsed = BlockSize;

    /// <summary>
    /// Seeds the cipher for a stream that begins at <paramref name="offset"/> bytes into the file.
    /// </summary>
    /// <param name="key">16 bytes, sent to the printer as hex in the command's <c>key</c> kwarg.</param>
    /// <param name="iv">16 bytes, sent as the <c>iv</c> kwarg and echoed back in the request URL.</param>
    /// <param name="offset">
    /// Where in the plaintext this stream starts. Must be a multiple of <see cref="BlockSize"/> -
    /// firmware asserts the same thing rather than handling a partial first block, and the printer
    /// only ever asks for sector-aligned ranges (transfer.cpp:205-206), so an unaligned offset means
    /// a bug on our side and not a case to support.
    /// </param>
    /// <exception cref="ArgumentException">A key or IV of the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A negative or unaligned offset.</exception>
    public TransferCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, long offset)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException($"The key must be exactly {KeyLength} bytes, not {key.Length}.", nameof(key));
        }

        if (iv.Length != IvLength)
        {
            throw new ArgumentException($"The IV must be exactly {IvLength} bytes, not {iv.Length}.", nameof(iv));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (offset % BlockSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                $"The offset must be a multiple of {BlockSize}: firmware's decryptor asserts the same alignment.");
        }

        _aes = Aes.Create();
        _aes.Key = key.ToArray();

        iv.CopyTo(_counter);

        // decrypt.cpp:23-28, byte for byte: the block index over the last four, most significant
        // first, with the leading twelve left as the IV supplied them.
        long blockIndex = offset / BlockSize;

        for (int i = BlockSize - 1; i >= BlockSize - CounterLength; i--)
        {
            _counter[i] = (byte)(blockIndex & 0xFF);
            blockIndex >>= 8;
        }
    }

    /// <summary>
    /// Transforms <paramref name="buffer"/> in place and advances the keystream by its length.
    /// </summary>
    /// <remarks>
    /// Buffering the keystream across calls is what lets a caller stream a range in whatever chunk
    /// sizes it likes: only the <i>first</i> offset has to be block-aligned, exactly as firmware's
    /// own <c>decrypt</c> is fed whatever a packet happened to carry.
    /// </remarks>
    public void Transform(Span<byte> buffer)
    {
        int position = 0;

        while (position < buffer.Length)
        {
            if (_keystreamUsed == BlockSize)
            {
                _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
                IncrementCounter();
                _keystreamUsed = 0;
            }

            int take = Math.Min(BlockSize - _keystreamUsed, buffer.Length - position);

            for (int i = 0; i < take; i++)
            {
                buffer[position + i] ^= _keystream[_keystreamUsed + i];
            }

            position += take;
            _keystreamUsed += take;
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_keystream);
        CryptographicOperations.ZeroMemory(_counter);
        _aes.Dispose();
    }

    /// <summary>
    /// Adds one to the counter block, carrying across all sixteen bytes.
    /// </summary>
    /// <remarks>
    /// Across <b>all</b> of them, not just the four the offset seeded: mbedtls carries out of the
    /// counter field and into the nonce (<c>mbedtls_aes_crypt_ctr</c>), so stopping the carry at byte
    /// twelve would diverge from firmware after 2^32 blocks. That is 64 GiB and unreachable through a
    /// <c>uint32</c> <c>orig_size</c> - matching anyway costs one loop bound and removes a footnote
    /// nobody would remember to check.
    /// </remarks>
    private void IncrementCounter()
    {
        for (int i = BlockSize - 1; i >= 0; i--)
        {
            if (++_counter[i] != 0)
            {
                break;
            }
        }
    }
}
