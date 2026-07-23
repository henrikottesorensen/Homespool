using System;
using System.Linq;
using System.Security.Cryptography;

namespace PrinterService.Host.PrusaConnect;

public class TokenService
{
    /// <summary>
    /// 128 bit salt.
    /// </summary>
    public const int SaltSize = 128 / 8;

    /// <summary>
    /// Prusa Firmware has a maximum length of 20 bytes.
    /// </summary>
    public const int TokenLength = 20;

    /// <summary>
    /// After Base64 encoding, that gives us 15 bytes (120 bit) of randomness.
    /// </summary>
    private const int TokenSize = TokenLength * 3 / 4;

    /// <summary>
    /// Hash length.
    /// </summary>
    public const int HashLength = 384 / 8;

    /// <summary>
    /// PBKDF2 Iterations.
    /// </summary>
    /// <remarks>
    /// Deliberately low by password-hashing standards (OWASP suggests six figures). That is sound
    /// <i>only</i> because the input is a <see cref="TokenSize"/>-byte CSPRNG value from
    /// <see cref="GenerateToken"/>: 120 bits of entropy is not brute-forceable at any work factor, so
    /// iterations buy nothing here and cost latency on every authenticated request. PBKDF2 is used for
    /// the salting and the constant-time envelope, not for key stretching.
    /// <para>
    /// <b>Do not reuse <see cref="HashToken(string)"/> for user passwords.</b> Against a
    /// human-chosen secret this iteration count is far too low, and the guard rails below
    /// (<see cref="MinimumIterations"/>) would happily accept it.
    /// </para>
    /// </remarks>
    private const int Iterations = 4096;

    /// <summary>
    /// Number of $ seperators in a hash.
    /// </summary>
    private const int HashSeperators = 5;

    /// <summary>
    /// Floor on the iteration count accepted from a stored hash.
    /// </summary>
    /// <remarks>
    /// The verification parameters travel with the hash so that changing <see cref="Iterations"/>
    /// does not invalidate tokens already issued. The cost of that flexibility is that the stored row
    /// dictates how verification runs, so the range is bounded at both ends: a floor stops a
    /// downgrade to a trivial work factor, and <see cref="MaximumIterations"/> stops a stored value
    /// from turning one unauthenticated request into unbounded CPU work.
    /// </remarks>
    private const int MinimumIterations = 4096;

    /// <summary>
    /// Ceiling on the iteration count accepted from a stored hash. Well above anything this service
    /// issues, low enough that a single verification cannot become a denial of service.
    /// </summary>
    private const int MaximumIterations = 1_000_000;

    /// <summary>
    /// SHA(3-)384 Hasher for data at rest security.
    /// </summary>
    public static readonly HashAlgorithmName HashAlgorithm = SHA3_384.IsSupported ?
                                                             HashAlgorithmName.SHA3_384 :
                                                             HashAlgorithmName.SHA384;

    /// <summary>
    /// Algorithm names accepted from a stored hash.
    /// </summary>
    /// <remarks>
    /// Both are listed rather than just <see cref="HashAlgorithm"/> so that hashes written on a
    /// SHA3-capable host still verify after a move to one without it, and vice versa. Anything else
    /// is refused: without this the algorithm name in the stored row selects the algorithm, and a
    /// row reading <c>$MD5$...</c> would be honoured.
    /// </remarks>
    private static readonly string[] SupportedHashAlgorithms =
    [
        HashAlgorithmName.SHA3_384.Name!,
        HashAlgorithmName.SHA384.Name!,
    ];

    public string GenerateToken()
    {
        byte[] tokenData = RandomNumberGenerator.GetBytes(TokenSize);

        return Convert.ToBase64String(tokenData);
    }

    /// <exception cref="ArgumentException">
    /// <paramref name="token"/> is not a base64 encoding of exactly <see cref="TokenSize"/> bytes.
    /// </exception>
    public string HashToken(string token)
    {
        if (!TryDecodeBase64(token, TokenSize, out byte[] tokenData))
        {
            // The token itself is never quoted back: it is a live credential, and this message ends
            // up in logs.
            throw new ArgumentException($"Token is not base64 of {TokenSize} bytes.", nameof(token));
        }

        return HashToken(tokenData);
    }

    public string HashToken(byte[] token)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(token, salt, Iterations, HashAlgorithm, HashLength);

        return $"${HashAlgorithm.Name}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}$";
    }

    /// <summary>
    /// Verifies a token supplied by a caller against a stored hash.
    /// </summary>
    /// <remarks>
    /// <paramref name="token"/> arrives straight off a request header and is therefore attacker
    /// controlled and unvalidated: every malformed shape has to return <c>false</c>, not throw. An
    /// exception here escapes the authentication handler and becomes a 500 on a pre-authentication
    /// path, which is both an unauthenticated crash and a way to tell malformed input apart from a
    /// wrong token.
    /// <para>
    /// <paramref name="knownHash"/> is ours and comes from the database, so a malformed one is a data
    /// integrity fault rather than an attack, and still throws.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="knownHash"/> is malformed.</exception>
    public bool VerifyToken(string? token, string knownHash)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > TokenLength)
        {
            return false;
        }

        if (knownHash.Count(c => c == '$') != HashSeperators)
        {
            // The hash is not echoed: it carries the salt and derived key, and this message is logged.
            throw new ArgumentException("Invalid hash format: unexpected segment count.", nameof(knownHash));
        }

        // Split knownHash into its components to extract settings.
        string[] split = knownHash.Split('$');
        string hashAlgorithm = split[1];

        if (!SupportedHashAlgorithms.Contains(hashAlgorithm, StringComparer.Ordinal))
        {
            throw new ArgumentException("Invalid hash format: unsupported hash algorithm.", nameof(knownHash));
        }

        if (!int.TryParse(split[2], out int iterations) ||
            iterations < MinimumIterations ||
            iterations > MaximumIterations)
        {
            throw new ArgumentException("Invalid hash format: iteration count missing or out of range.", nameof(knownHash));
        }

        if (!TryDecodeBase64(split[3], SaltSize, out byte[] salt) ||
            !TryDecodeBase64(split[4], HashLength, out byte[] hash))
        {
            throw new ArgumentException("Invalid hash format: salt or hash is not base64 of the expected length.", nameof(knownHash));
        }

        // Caller-supplied, so a bad encoding is a failed authentication rather than an exception.
        if (!TryDecodeBase64(token, TokenSize, out byte[] tokenData))
        {
            return false;
        }

        // Hash supplied token with knownHash's settings.
        byte[] hashedInputToken = Rfc2898DeriveBytes.Pbkdf2(tokenData, salt, iterations, new HashAlgorithmName(hashAlgorithm), HashLength);

        // Compare hashed input token with known hash value.
        return CryptographicOperations.FixedTimeEquals(hashedInputToken, hash);
    }

    /// <summary>
    /// Decodes base64 that is expected to be exactly <paramref name="expectedLength"/> bytes, without
    /// throwing on malformed input.
    /// </summary>
    /// <remarks>
    /// The length is part of the test rather than a separate check. Every value passed through here
    /// has a fixed size, so a short or long decode is malformed by definition, and rejecting it up
    /// front keeps a wrong-sized token from reaching PBKDF2.
    /// </remarks>
    private static bool TryDecodeBase64(string? value, int expectedLength, out byte[] decoded)
    {
        decoded = new byte[expectedLength];

        return value is not null &&
               Convert.TryFromBase64String(value, decoded, out int written) &&
               written == expectedLength;
    }
}
