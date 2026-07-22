using System;
using System.Linq;
using System.Security.Cryptography;

namespace PrinterService.Api.PrusaConnect;

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
    /// PBKDF2 Iterations
    /// </summary>
    private const int Iterations = 4096;

    /// <summary>
    /// Number of $ seperators in a hash.
    /// </summary>
    private const int HashSeperators = 5;
    
    /// <summary>
    /// SHA(3-)384 Hasher for data at rest security.
    /// </summary>
    public static readonly HashAlgorithmName HashAlgorithm = SHA3_384.IsSupported ?
                                                             HashAlgorithmName.SHA3_384 :
                                                             HashAlgorithmName.SHA384;
    
    public string GenerateToken()
    {
        byte[] tokenData = RandomNumberGenerator.GetBytes(TokenSize);
        
        return Convert.ToBase64String(tokenData);
    }

    public string HashToken(string token)
    {
        byte[] tokenData = Convert.FromBase64String(token);

        return HashToken(tokenData);
    }
    
    public string HashToken(byte[] token)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(token, salt, Iterations, HashAlgorithm, HashLength);

        return $"${HashAlgorithm.Name}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}$";
    }

    public bool VerifyToken(string? token, string knownHash) 
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > TokenLength)
        {
            return false;
        }

        if (knownHash.Count(c => c == '$') != HashSeperators)
        {
            throw new ArgumentException($"Invalid hash format: {knownHash}");
        }
        
        byte[] tokenData = Convert.FromBase64String(token);
        
        // Split knownHash into its components to extract settings.
        string[] split = knownHash.Split('$');
        string hashAlgorithm =  split[1];
        int iterations = int.Parse(split[2]);
        byte[] salt = Convert.FromBase64String(split[3]);
        byte[] hash = Convert.FromBase64String(split[4]);
        
        // Hash supplied token with knownHash's settings.
        byte[] hashedInputToken = Rfc2898DeriveBytes.Pbkdf2(tokenData, salt, iterations, new HashAlgorithmName(hashAlgorithm), HashLength);
        
        // Compare hashed input token with known hash value.
        return CryptographicOperations.FixedTimeEquals(hashedInputToken, hash);
    }
}
