using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PrinterService.Api.PrusaConnect;

public class CodeGenerator
{
    /// <summary>
    /// Maximum code Length supported by Prusa Firmware 
    /// </summary>
    private const int CodeLength = 24;
    
    /// <summary>
    /// Length of one time random number.
    /// </summary>
    private const int NonceBytes = 128 / 8;
    
    public string GenerateCode(string printerSerialNumber)
    {
        using SHA3_384 hasher = SHA3_384.Create();
        using RandomNumberGenerator rng = RandomNumberGenerator.Create();

        byte[] serial = Encoding.UTF8.GetBytes($"{printerSerialNumber}");
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        
        byte[] hash = hasher.ComputeHash(serial.Concat(nonce).ToArray());
        
        return SimpleBase.Base36.UpperCase.Encode(hash).Substring(0, CodeLength);
    }
}
