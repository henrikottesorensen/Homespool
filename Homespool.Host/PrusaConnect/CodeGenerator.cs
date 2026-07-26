using System.Security.Cryptography;
using System.Text;

namespace Homespool.Host.PrusaConnect;

public class CodeGenerator
{
    /// <summary>
    /// Maximum code Length supported by Prusa Firmware.
    /// </summary>
    private const int CodeLength = 24;

    /// <summary>
    /// Length of one time random number.
    /// </summary>
    private const int NonceBytes = 128 / 8;

    /// <summary>
    /// SHA(3-)384, falling back where the platform has no SHA-3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET defers hashing to the OS libraries, so SHA-3 is only present where the OS provides it:
    /// Windows 11 build 25324+, or Linux with OpenSSL 1.1.1+. Apple platforms have no SHA-3 at all,
    /// and .NET 10 removed the last of the macOS OpenSSL fallbacks, so installing OpenSSL by hand
    /// does not restore it. Calling <c>SHA3_384.Create()</c> unguarded therefore threw
    /// <see cref="System.PlatformNotSupportedException"/> on every macOS machine, which took out
    /// printer registration entirely - the container image (mcr.microsoft.com/dotnet/aspnet:10.0,
    /// Debian, OpenSSL 3) hid it, because production is the one platform where it works.
    /// See https://learn.microsoft.com/dotnet/standard/security/cross-platform-cryptography#sha-3.
    /// </para>
    /// <para>
    /// The choice is not security relevant here: the code's unpredictability comes entirely from
    /// <see cref="NonceBytes"/> bytes of CSPRNG output, and the hash only spreads that over the
    /// output alphabet. Either algorithm is a fine way to do that, which is why falling back is
    /// safe rather than a downgrade. It is duplicated from <see cref="TokenService.HashAlgorithm"/>
    /// rather than shared precisely because the two are unrelated decisions - that one selects a
    /// PBKDF2 PRF and must not silently change code generation if it is ever revisited.
    /// </para>
    /// </remarks>
    private static readonly HashAlgorithmName HashAlgorithm = SHA3_384.IsSupported ?
                                                              HashAlgorithmName.SHA3_384 :
                                                              HashAlgorithmName.SHA384;

    public string GenerateCode(string printerSerialNumber)
    {
        byte[] serial = Encoding.UTF8.GetBytes(printerSerialNumber);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);

        // Each .. spreads the array's elements rather than the array itself, hashing serial
        // followed by nonce.
        byte[] hash = CryptographicOperations.HashData(HashAlgorithm, [.. serial, .. nonce]);

        return SimpleBase.Base36.UpperCase.Encode(hash).Substring(0, CodeLength);
    }
}
