using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

namespace Homespool.Host.Accounts;

/// <summary>
/// An account's unspent recovery codes as <see cref="HSUserStore"/> stores them: one salt and one
/// iteration count for the set, and a PBKDF2-HMAC-SHA512 hash per code.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stored form is <c>v1$iterations$salt$hash,hash,…</c></b>, base64 for the binary parts, and an
/// empty string for a set with nothing left in it - the same value the framework's store writes for no
/// codes. Neither <c>$</c> nor <c>,</c> occurs in base64, and <c>$</c> never occurs in the framework's
/// plaintext list, which is how <see cref="IsHashed"/> tells the two apart.
/// </para>
/// <para>
/// <b>Why it is stretched at all.</b> The framework mints a code as ten characters from a 26-character
/// alphabet, about 2^47. Salted but unstretched, a guess costs one hash, and 2^47 hashes is within reach
/// of a single GPU; <see cref="Iterations"/> multiplies every guess by that count.
/// </para>
/// <para>
/// <b>One salt for the set, not one per code.</b> Redeeming derives the typed code once and compares it
/// with every hash. Salting each code apart would stop an attacker testing one guess against all ten,
/// about five times their work - but would cost the deployment ten derivations per attempt instead of
/// one, and iterations buy the same work more cheaply. On a Raspberry Pi 3 one derivation at this count
/// takes about a tenth of a second: once per attempt, and once per code when a set is minted.
/// </para>
/// <para>
/// <b>The iteration count is stored with each set</b>, so raising <see cref="Iterations"/> changes the
/// sets minted afterwards and leaves every existing one readable.
/// </para>
/// </remarks>
internal sealed class RecoveryCodeHashes
{
    /// <summary>The iteration count a newly minted set is hashed with.</summary>
    public const int Iterations = 20_000;

    private const string Version = "v1";
    private const char FieldSeparator = '$';
    private const char HashSeparator = ',';
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly int _iterations;
    private readonly byte[] _salt;
    private readonly List<byte[]> _hashes;

    private RecoveryCodeHashes(int iterations, byte[] salt, List<byte[]> hashes)
    {
        _iterations = iterations;
        _salt = salt;
        _hashes = hashes;
    }

    /// <summary>How many codes are left unspent.</summary>
    public int Count => _hashes.Count;

    /// <summary>
    /// Whether <paramref name="stored"/> is in this form, rather than the framework's plaintext list.
    /// </summary>
    /// <param name="stored">The token row's value.</param>
    public static bool IsHashed(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return stored.Contains(FieldSeparator, StringComparison.Ordinal);
    }

    /// <summary>Hashes <paramref name="codes"/> under a fresh salt.</summary>
    /// <param name="codes">The codes in the clear, as they are about to be shown to their owner.</param>
    public static RecoveryCodeHashes Create(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);

        return new RecoveryCodeHashes(Iterations,
                                      salt,
                                      codes.Distinct(StringComparer.Ordinal)
                                           .Select(code => Derive(code, salt, Iterations))
                                           .ToList());
    }

    /// <summary>
    /// Reads a stored set, or answers <see langword="null"/> when <paramref name="stored"/> is not one
    /// this version can read.
    /// </summary>
    /// <param name="stored">A value <see cref="IsHashed"/> accepted.</param>
    public static RecoveryCodeHashes? Parse(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        string[] fields = stored.Split(FieldSeparator);

        if (fields.Length != 4 ||
            fields[0] != Version ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations) ||
            iterations <= 0 ||
            FromBase64(fields[2], SaltBytes) is not { } salt)
        {
            return null;
        }

        List<byte[]> hashes = [];

        foreach (string field in fields[3].Split(HashSeparator))
        {
            if (FromBase64(field, HashBytes) is not { } hash)
            {
                return null;
            }

            hashes.Add(hash);
        }

        return new RecoveryCodeHashes(iterations, salt, hashes);
    }

    /// <summary>
    /// Spends <paramref name="code"/> if it is one of the set, answering whether it was.
    /// </summary>
    /// <param name="code">The code as its owner typed it, spaces already removed.</param>
    public bool TryRemove(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        byte[] candidate = Derive(code, _salt, _iterations);
        int index = _hashes.FindIndex(hash => CryptographicOperations.FixedTimeEquals(hash, candidate));

        if (index < 0)
        {
            return false;
        }

        _hashes.RemoveAt(index);

        return true;
    }

    /// <summary>The value to store: this set in its stored form, or an empty string when nothing is left.</summary>
    public string ToStored()
    {
        if (_hashes.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(FieldSeparator,
                           Version,
                           _iterations.ToString(CultureInfo.InvariantCulture),
                           Convert.ToBase64String(_salt),
                           string.Join(HashSeparator, _hashes.Select(Convert.ToBase64String)));
    }

    private static byte[] Derive(string code, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(code, salt, iterations, HashAlgorithmName.SHA512, HashBytes);
    }

    private static byte[]? FromBase64(string field, int length)
    {
        Span<byte> buffer = stackalloc byte[length + 3];

        return Convert.TryFromBase64String(field, buffer, out int written) && written == length ?
                   buffer[..written].ToArray() :
                   null;
    }
}
