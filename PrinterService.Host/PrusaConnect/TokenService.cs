using System;
using System.Buffers.Text;
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
    public const int PrinterTokenLength = 20;

    /// <summary>
    /// After Base64 encoding, that gives us 15 bytes (120 bit) of randomness.
    /// </summary>
    private const int TokenSize = PrinterTokenLength * 3 / 4;

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

    private const int MaximumTokenLength = 128;

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
        return GenerateToken(TokenSize);
    }
    
    public string GenerateToken(int bytes)
    {
        byte[] tokenData = RandomNumberGenerator.GetBytes(bytes);

        return Base64Url.EncodeToString(tokenData);
    }

    public string HashToken(string token)
    {
        if (token.Length is < PrinterTokenLength or > MaximumTokenLength)
        {
            throw new ArgumentException("Invalid token: unexpected length.", nameof(token));
        }

        if (TryDecodeBase64(token, out Span<byte> tokenData))
        {
            return HashToken(tokenData);
        }
        
        throw new ArgumentException("Invalid token: Could not decode.", nameof(token));
    }

    private string HashToken(Span<byte> token)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(token, salt, Iterations, HashAlgorithm, HashLength);

        return $"${HashAlgorithm.Name}${Iterations}${Base64Url.EncodeToString(salt)}${Base64Url.EncodeToString(key)}$";
    }

    /// <summary>
    /// Verifies a token supplied by a caller against a stored hash.
    /// </summary>
    /// <remarks>
    /// <paramref name="token"/> is attacker-controlled and off a request header, so every malformed
    /// shape returns <c>false</c>; a throw here would be an unauthenticated 500. <paramref name="knownHash"/>
    /// is ours, so a malformed one is a data fault and throws.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="knownHash"/> is malformed.</exception>
    public bool VerifyToken(string? token, string knownHash)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < PrinterTokenLength or > MaximumTokenLength)
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

        if (!TryDecodeBase64(split[3], out Span<byte> salt) ||
            !TryDecodeBase64(split[4], out Span<byte> hash))
        {
            throw new ArgumentException("Invalid hash format: salt or hash is not valid base64.", nameof(knownHash));
        }

        // Caller-supplied, so a bad encoding is a failed authentication rather than an exception.
        if (!TryDecodeBase64(token, out Span<byte> tokenData))
        {
            return false;
        }

        // Hash supplied token with knownHash's settings.
        byte[] hashedInputToken = Rfc2898DeriveBytes.Pbkdf2(tokenData, salt, iterations, new HashAlgorithmName(hashAlgorithm), HashLength);

        // Compare hashed input token with known hash value.
        return CryptographicOperations.FixedTimeEquals(hashedInputToken, hash);
    }

    /// <remarks>
    /// <see cref="Base64Url.TryDecodeFromChars"/> is not used: despite the name it throws
    /// <see cref="FormatException"/> on malformed input, which on the token path would escape as a
    /// pre-authentication 500. <see cref="Base64Url.IsValid(ReadOnlySpan{char})"/> is the non-throwing
    /// gate.
    /// </remarks>
    private static bool TryDecodeBase64(string value, out Span<byte> output)
    {
        if (Base64Url.IsValid(value))
        {
            output = Base64Url.DecodeFromChars(value);
            return true;
        }

        output = default;
        return false;
    }
}
