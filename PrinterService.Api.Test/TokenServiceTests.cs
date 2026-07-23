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

    /// <summary>
    /// A token that is not valid base64 fails verification instead of throwing.
    /// </summary>
    /// <remarks>
    /// The token arrives as a raw request header, so this is reachable by anyone who can open a
    /// socket, before any authentication has happened. Previously <c>Convert.FromBase64String</c>
    /// threw <see cref="FormatException"/> straight out of the authentication handler, turning
    /// <c>Token: hello!</c> into a 500 - an unauthenticated crash, and an oracle distinguishing
    /// malformed input from a wrong token.
    /// </remarks>
    [Theory]
    [InlineData("hello!")]
    [InlineData("not base64")]
    [InlineData("====")]
    [InlineData("A")]
    [InlineData("\0\0\0\0")]
    [InlineData("AAAA====AAAA")]
    public void MalformedTokenDoesNotVerifyAndDoesNotThrow(string malformedToken)
    {
        string hash = _tokenService.HashToken(_tokenService.GenerateToken());
        Func<bool> verify = () => _tokenService.VerifyToken(malformedToken, hash);

        verify.Should().NotThrow();
        verify().Should().BeFalse();
    }

    /// <summary>
    /// Well-formed base64 of the wrong length fails verification.
    /// </summary>
    /// <remarks>
    /// Tokens are a fixed size, so a decode of any other length is malformed by definition and is
    /// rejected before it reaches PBKDF2.
    /// </remarks>
    [Fact]
    public void WrongLengthTokenDoesNotVerify()
    {
        string hash = _tokenService.HashToken(_tokenService.GenerateToken());

        _tokenService.VerifyToken("AAAA", hash).Should().BeFalse();
        _tokenService.VerifyToken(Convert.ToBase64String(RandomNumberGenerator.GetBytes(3)), hash).Should().BeFalse();
    }

    /// <summary>
    /// <see cref="TokenService.HashToken(string)"/> rejects a non-base64 token by argument, and does
    /// not quote the token back in the message.
    /// </summary>
    [Fact]
    public void HashTokenRejectsNonBase64Token()
    {
        Action hashToken = () => _tokenService.HashToken("not base64!");

        hashToken.Should().Throw<ArgumentException>()
                 .Which.Message.Should().NotContain("not base64!");
    }

    /// <summary>
    /// A stored hash naming an algorithm this service does not issue is refused.
    /// </summary>
    /// <remarks>
    /// The verification parameters are read out of the stored row, so absent a whitelist the row
    /// decides how verification runs. Anyone able to write to the database - SQL injection, a
    /// restored backup, direct access to the SQLite file - could rewrite the envelope to a broken
    /// algorithm and precompute against it.
    /// </remarks>
    [Theory]
    [InlineData("MD5")]
    [InlineData("SHA1")]
    [InlineData("")]
    [InlineData("NOTAHASH")]
    public void UnsupportedHashAlgorithmIsRejected(string algorithm)
    {
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');

        string tampered = $"${algorithm}${split[2]}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        verify.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A stored iteration count outside the accepted range, or not a number at all, is refused.
    /// </summary>
    /// <remarks>
    /// The floor stops a downgrade to a trivial work factor. The ceiling matters more: the iteration
    /// count is applied to an unauthenticated request, so an unbounded value turns one request into
    /// as much CPU work as the stored row asks for.
    /// </remarks>
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2000000000")]
    [InlineData("not a number")]
    [InlineData("")]
    public void OutOfRangeIterationCountIsRejected(string iterations)
    {
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');

        string tampered = $"${split[1]}${iterations}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        verify.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A stored salt or digest that is not base64 of the expected length is refused as malformed,
    /// rather than throwing <see cref="FormatException"/> from inside the decode.
    /// </summary>
    [Theory]
    [InlineData(3, "not base64")]
    [InlineData(3, "AAAA")]
    [InlineData(4, "not base64")]
    [InlineData(4, "AAAA")]
    public void MalformedSaltOrHashIsRejected(int segment, string value)
    {
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');
        split[segment] = value;

        string tampered = $"${split[1]}${split[2]}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        verify.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The malformed-hash exception does not carry the hash itself.
    /// </summary>
    /// <remarks>
    /// The envelope holds the salt and the derived key, and this message reaches the log sink like
    /// any other. The row is identifiable from the surrounding request without reproducing its
    /// contents.
    /// </remarks>
    [Fact]
    public void MalformedHashExceptionDoesNotLeakTheHash()
    {
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');

        string tampered = $"$MD5${split[2]}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        string message = verify.Should().Throw<ArgumentException>().Which.Message;

        message.Should().NotContain(split[3]);
        message.Should().NotContain(split[4]);
        message.Should().NotContain(tampered);
    }

    /// <summary>
    /// A hash issued with a different but still supported algorithm and a different iteration count
    /// continues to verify.
    /// </summary>
    /// <remarks>
    /// The point of carrying the parameters alongside the hash. The whitelist and the range check
    /// must not turn into a migration trap: tokens issued before a parameter change, or on a host
    /// with different SHA3 support, still have to authenticate.
    /// </remarks>
    [Fact]
    public void SupportedAlgorithmWithDifferentIterationsStillVerifies()
    {
        string token = _tokenService.GenerateToken();
        byte[] tokenData = Convert.FromBase64String(token);
        byte[] salt = RandomNumberGenerator.GetBytes(TokenService.SaltSize);

        const int otherIterations = 8192;
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(tokenData, salt, otherIterations, HashAlgorithmName.SHA384, TokenService.HashLength);

        string hash = $"${HashAlgorithmName.SHA384.Name}${otherIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}$";

        _tokenService.VerifyToken(token, hash).Should().BeTrue();
    }
}
