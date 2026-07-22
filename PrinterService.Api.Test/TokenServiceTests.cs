using System.Security.Cryptography;

using AwesomeAssertions;

using PrinterService.Api.PrusaConnect;

namespace PrinterService.Api.Test;

public class TokenServiceTests
{
    private readonly TokenService  _tokenService = new();
    
    [Fact]
    public void GenerateToken()
    {
        string token = _tokenService.GenerateToken();
        Action convertToByteArray = () => Convert.FromBase64String(token);
        
        token.Should().NotBeNullOrWhiteSpace();
        token.Length.Should().BeLessThanOrEqualTo(TokenService.TokenLength);
        convertToByteArray.Should().NotThrow();
    }

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

    [Fact]
    public void TokenVerifies()
    {
        string token = _tokenService.GenerateToken();
        string hash = _tokenService.HashToken(token);

        _tokenService.VerifyToken(token, hash).Should().BeTrue();
    }
    
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
