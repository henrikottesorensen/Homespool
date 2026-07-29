using System.Security.Cryptography;
using System.Text;

namespace Homespool.Host.PrusaConnect;

public class CodeGenerator
{
    /// <summary>
    /// How many characters a registration code is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten Crockford base32 characters is 2^50. The bar is resisting online guessing of a pending
    /// code inside its lifetime (<see cref="PrusaConnectOptions.RegistrationCodeLifetimeMinutes"/>,
    /// 30 minutes): the anonymous <c>/p/register</c> endpoints sit behind a 300/min limiter, so an
    /// attacker gets ~9 000 attempts against ~1.1x10^15 - about one in 10^11. The claim page has no
    /// limiter of its own, which is what <see cref="ClaimAttemptLimiter"/> is for.
    /// </para>
    /// <para>
    /// It was 24 base36 characters, which is ~2^124 - some 74 bits more than anything here needs,
    /// and unusable: the printer's QR is hardcoded to Prusa's servers and cannot be redirected, so
    /// <b>every Homespool user types this by hand off a low-resolution LCD</b>. On 2026-07-28 that
    /// cost a real failed enrollment, reading the <c>O</c> in <c>BNK6BD5CXLMMNQQOD0UL5MIQ</c> as a
    /// <c>0</c>. Ten characters is also exactly what Prusa's own servers issue (<c>MUF4RZJF5R</c> in
    /// <c>enrol.cap</c>), and stays well inside the firmware's <c>CODE_SIZE = 25</c> buffer.
    /// </para>
    /// </remarks>
    private const int CodeLength = 10;

    /// <summary>
    /// Length of one time random number.
    /// </summary>
    private const int NonceBytes = 128 / 8;

    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA384;

    public string GenerateCode(string printerSerialNumber)
    {
        byte[] serial = Encoding.UTF8.GetBytes(printerSerialNumber);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);

        // Each .. spreads the array's elements rather than the array itself, hashing serial
        // followed by nonce.
        byte[] hash = CryptographicOperations.HashData(HashAlgorithm, [.. serial, .. nonce]);

        // Crockford base32 rather than base36, because base36 contains every confusable pair at
        // once - O/0 (which bit a real enrollment), and I/1, S/5, B/8 waiting their turn. Crockford
        // omits I, L, O and U entirely, so the misread that cost 2026-07-28 is unrepresentable
        // rather than merely less likely; ClaimCode.Normalise then maps the substitutions on input.
        // The encoder already emits uppercase - its alphabet is the literal
        // "0123456789ABCDEFGHJKMNPQRSTVWXYZ" (SimpleBase, Base32Alphabet.cs) - so the uppercasing is
        // belt-and-braces. It is kept because the stored value must match what ClaimCode.Normalise
        // produces, and that is worth holding locally rather than inheriting from a dependency.
        return SimpleBase.Base32.Crockford.Encode(hash).ToUpperInvariant().Substring(0, CodeLength);
    }
}
