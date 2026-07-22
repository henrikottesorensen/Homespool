using System.Security.Cryptography;

using AwesomeAssertions;

using PrinterService.Api.PrusaConnect;

namespace PrinterService.Api.Test;

public class TokenServiceTests
{
    private readonly TokenService  _tokenService = new();
    
    /// <summary>
    /// A generated token is non-empty, within the declared length, and valid base64.
    /// </summary>
    /// <remarks>
    /// The token is returned to the printer in a <c>Token</c> header and stored by it verbatim, so it
    /// has to be header-safe and bounded. Buddy copies it into a fixed buffer.
    /// </remarks>
    [Fact]
    public void GenerateToken()
    {
        string token = _tokenService.GenerateToken();
        Action convertToByteArray = () => Convert.FromBase64String(token);
        
        token.Should().NotBeNullOrWhiteSpace();
        token.Length.Should().BeLessThanOrEqualTo(TokenService.TokenLength);
        convertToByteArray.Should().NotThrow();
    }

    /// <summary>
    /// The stored hash is the expected <c>$</c>-delimited envelope, not a bare digest.
    /// </summary>
    /// <remarks>
    /// Six segments carrying the algorithm name, iteration count, salt and hash. Verifying the shape
    /// matters because the parameters have to travel <i>with</i> the hash: changing the algorithm or
    /// iteration count later must not invalidate tokens already issued.
    /// </remarks>
    [Fact]
    public void HashToken()
    {
        string token = _tokenService.GenerateToken();
        string hash = _tokenService.HashToken(token);
        
        hash.Should().NotBeNullOrWhiteSpace();
        
        string[] split = hash.Split('$');
        split.Length.Should().Be(6);
        
        split[1].Should().Be(TokenService.HashAlgorithm.Name);
        split[2].All(char.IsAsciiDigit).Should().BeTrue();
        Convert.FromBase64String(split[3]).Length.Should().Be(TokenService.SaltSize);
        Convert.FromBase64String(split[4]).Length.Should().Be(TokenService.HashLength);
    }

    /// <summary>
    /// A token verifies against its own hash.
    /// </summary>
    /// <remarks>
    /// The round trip that the printer authentication handler performs on every request.
    /// </remarks>
    [Fact]
    public void TokenVerifies()
    {
        string token = _tokenService.GenerateToken();
        string hash = _tokenService.HashToken(token);

        _tokenService.VerifyToken(token, hash).Should().BeTrue();
    }
    
    /// <summary>
    /// A wrong token, an empty string and null all fail verification.
    /// </summary>
    /// <remarks>
    /// The empty and null cases are the interesting ones: a missing <c>Token</c> header must not
    /// authenticate. Buddy sends the header on every request, but the server cannot assume it.
    /// </remarks>
    [Fact]
    public void InvalidTokenDoesNotVerify()
    {
        string token = _tokenService.GenerateToken();
        string hash = _tokenService.HashToken(token);

        string invalidToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenService.TokenLength));
        _tokenService.VerifyToken(invalidToken, hash).Should().BeFalse();
        
        _tokenService.VerifyToken(string.Empty, hash).Should().BeFalse();
        _tokenService.VerifyToken(null, hash).Should().BeFalse();
    }
}
