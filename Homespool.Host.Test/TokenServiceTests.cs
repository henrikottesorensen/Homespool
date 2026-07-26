using System;
using System.Buffers.Text;
using System.Linq;
using System.Security.Cryptography;

using AwesomeAssertions;

using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Test;

public class TokenServiceTests
{
    private readonly TokenService _tokenService = new();

    /// <summary>
    /// A generated token is non-empty, exactly the printer length, and uses the URL-safe base64
    /// alphabet.
    /// </summary>
    /// <remarks>
    /// The token is returned to the printer in a <c>Token</c> header and stored by it verbatim, so it
    /// has to be header-safe and bounded; Buddy copies it into a fixed buffer. URL-safe base64 keeps it
    /// free of <c>+</c>, <c>/</c> and <c>=</c>.
    /// </remarks>
    [Fact]
    public void GenerateToken()
    {
        // Act
        string token = _tokenService.GenerateToken();
        Action decode = () => Base64Url.DecodeFromChars(token);

        // Assert
        token.Should().NotBeNullOrWhiteSpace();
        token.Length.Should().Be(TokenService.PrinterTokenLength);
        token.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        decode.Should().NotThrow();
    }

    /// <summary>
    /// <see cref="TokenService.GenerateToken(int)"/> honors the requested byte count rather than always
    /// generating <see cref="TokenService.PrinterTokenLength"/>-derived bytes.
    /// </summary>
    /// <remarks>
    /// <see cref="Homespool.Host.Services.InvitationService.CreateAsync"/> calls this overload with
    /// <c>InviteTokenLength = 32</c>, expecting a 32-byte token; a mint that silently ignored the
    /// parameter would issue shorter, weaker invite tokens without any visible failure.
    /// </remarks>
    [Theory]
    [InlineData(32)]
    [InlineData(15)]
    [InlineData(1)]
    public void GenerateTokenWithExplicitLengthReturnsThatManyBytes(int bytes)
    {
        // Act
        string token = _tokenService.GenerateToken(bytes);

        // Assert
        Base64Url.DecodeFromChars(token).Length.Should().Be(bytes);
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
        // Act
        string token = _tokenService.GenerateToken();
        string hash = _tokenService.HashToken(token);

        // Assert
        hash.Should().NotBeNullOrWhiteSpace();

        string[] split = hash.Split('$');
        split.Length.Should().Be(6);

        split[1].Should().Be(TokenService.HashAlgorithm.Name);
        split[2].All(char.IsAsciiDigit).Should().BeTrue();
        Base64Url.DecodeFromChars(split[3]).Length.Should().Be(TokenService.SaltSize);
        Base64Url.DecodeFromChars(split[4]).Length.Should().Be(TokenService.HashLength);
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
        // Arrange
        string token = _tokenService.GenerateToken();
        string hash = _tokenService.HashToken(token);

        // Assert
        _tokenService.VerifyToken(token, hash).Should().BeTrue();
    }

    /// <summary>
    /// An invite-length token round-trips through the same single-length methods the printer path uses.
    /// </summary>
    /// <remarks>
    /// Invitations no longer have a dedicated expected-length overload; they mint a longer (32-byte)
    /// token via <see cref="TokenService.GenerateToken(int)"/> and then share
    /// <see cref="TokenService.HashToken(string)"/> and <see cref="TokenService.VerifyToken"/>, so that
    /// length has to hash and verify within the accepted bound.
    /// </remarks>
    [Fact]
    public void InviteLengthTokenVerifies()
    {
        // Arrange
        string token = _tokenService.GenerateToken(32);
        string hash = _tokenService.HashToken(token);

        // Assert
        _tokenService.VerifyToken(token, hash).Should().BeTrue();
    }

    /// <summary>
    /// A different token, an empty string and null all fail verification.
    /// </summary>
    /// <remarks>
    /// The other-token case is well-formed, so it decodes cleanly and fails at the constant-time
    /// compare rather than short-circuiting earlier. The empty and null cases guard the missing
    /// <c>Token</c> header: Buddy sends it on every request, but the server cannot assume it.
    /// </remarks>
    [Fact]
    public void InvalidTokenDoesNotVerify()
    {
        // Arrange
        string token = _tokenService.GenerateToken();
        string hash = _tokenService.HashToken(token);

        // Assert
        string otherToken = _tokenService.GenerateToken();
        _tokenService.VerifyToken(otherToken, hash).Should().BeFalse();

        _tokenService.VerifyToken(string.Empty, hash).Should().BeFalse();
        _tokenService.VerifyToken(null, hash).Should().BeFalse();
    }

    /// <summary>
    /// A malformed token fails verification instead of throwing.
    /// </summary>
    /// <remarks>
    /// The token arrives as a raw request header, reachable before any authentication. It must never
    /// throw: a short or oversized token is turned away by the length guard, and a bad-alphabet one by
    /// the non-throwing base64 decode. Either way the handler sees <c>false</c>, not a 500 that would
    /// also distinguish malformed input from a wrong token.
    /// </remarks>
    [Theory]
    [InlineData("hello!")]
    [InlineData("not base64")]
    [InlineData("====")]
    [InlineData("A")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAABBBBCCCC++//DDDD")]
    public void MalformedTokenDoesNotVerifyAndDoesNotThrow(string malformedToken)
    {
        // Arrange
        string hash = _tokenService.HashToken(_tokenService.GenerateToken());
        Func<bool> verify = () => _tokenService.VerifyToken(malformedToken, hash);

        // Assert
        verify.Should().NotThrow();
        verify().Should().BeFalse();
    }

    /// <summary>
    /// A token outside the accepted length bound fails verification without throwing.
    /// </summary>
    /// <remarks>
    /// The exact byte size is no longer re-checked at decode; the guard is the
    /// [<see cref="TokenService.PrinterTokenLength"/>, MaximumTokenLength] character bound, which keeps
    /// an undersized or oversized token off PBKDF2. An in-range token of the wrong length still fails,
    /// just later, at the constant-time compare.
    /// </remarks>
    [Fact]
    public void TokenOutsideLengthBoundsDoesNotVerify()
    {
        // Arrange
        string hash = _tokenService.HashToken(_tokenService.GenerateToken());

        // Assert - below PrinterTokenLength.
        _tokenService.VerifyToken("AAAA", hash).Should().BeFalse();

        // Above the maximum accepted length.
        _tokenService.VerifyToken(new string('A', 129), hash).Should().BeFalse();

        // In range but the wrong length: decodes, reaches PBKDF2, fails closed at the compare.
        _tokenService.VerifyToken(_tokenService.GenerateToken(24), hash).Should().BeFalse();
    }

    /// <summary>
    /// <see cref="TokenService.HashToken(string)"/> rejects a token that is the wrong length or not
    /// decodable, and does not quote the token back in the message.
    /// </summary>
    /// <remarks>
    /// Unlike verification, hashing is never on an attacker path - it runs when <i>we</i> mint a
    /// credential - so a bad token is a programming error and throws. The token is a live secret, so
    /// the message must not echo it.
    /// </remarks>
    [Theory]
    [InlineData("short")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!")]
    public void HashTokenRejectsInvalidToken(string token)
    {
        // Arrange
        Action hashToken = () => _tokenService.HashToken(token);

        // Assert
        hashToken.Should().Throw<ArgumentException>()
                 .Which.Message.Should().NotContain(token);
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
        // Arrange
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');

        string tampered = $"${algorithm}${split[2]}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        // Assert
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
        // Arrange
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');

        string tampered = $"${split[1]}${iterations}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        // Assert
        verify.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A stored salt or digest that is not decodable base64 is rejected as a data fault, rather than
    /// surfacing <see cref="FormatException"/> from inside the decode.
    /// </summary>
    /// <remarks>
    /// <c>knownHash</c> is ours, so an undecodable segment is corruption, not attacker input, and
    /// throwing is the right signal. The <c>+</c>/<c>/</c>/<c>=</c> cases also pin the URL-safe
    /// alphabet: standard-base64 punctuation is not accepted here.
    /// </remarks>
    [Theory]
    [InlineData(3, "not base64")]
    [InlineData(3, "AAAA+/==")]
    [InlineData(4, "not base64")]
    [InlineData(4, "AAAA+/==")]
    public void NonBase64SaltOrHashIsRejected(int segment, string value)
    {
        // Arrange
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');
        split[segment] = value;

        string tampered = $"${split[1]}${split[2]}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        // Assert
        verify.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// A stored salt or digest that is valid base64 but the wrong length fails verification closed,
    /// without throwing.
    /// </summary>
    /// <remarks>
    /// Exact length is no longer re-validated on the trusted hash, and it does not need to be: it can
    /// never cause a false accept. <see cref="Rfc2898DeriveBytes"/> always emits
    /// <see cref="TokenService.HashLength"/> bytes and
    /// <see cref="CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte},System.ReadOnlySpan{byte})"/>
    /// requires equal length, so a mis-sized digest simply compares unequal.
    /// </remarks>
    [Theory]
    [InlineData(3, "AAAA")]
    [InlineData(4, "AAAA")]
    public void WrongLengthSaltOrHashFailsVerification(int segment, string value)
    {
        // Arrange
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');
        split[segment] = value;

        string tampered = $"${split[1]}${split[2]}${split[3]}${split[4]}$";
        Func<bool> verify = () => _tokenService.VerifyToken(token, tampered);

        // Assert
        verify.Should().NotThrow();
        verify().Should().BeFalse();
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
        // Arrange
        string token = _tokenService.GenerateToken();
        string[] split = _tokenService.HashToken(token).Split('$');

        string tampered = $"$MD5${split[2]}${split[3]}${split[4]}$";
        Action verify = () => _tokenService.VerifyToken(token, tampered);

        // Act
        string message = verify.Should().Throw<ArgumentException>().Which.Message;

        // Assert
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
        // Arrange
        string token = _tokenService.GenerateToken();
        byte[] tokenData = Base64Url.DecodeFromChars(token);
        byte[] salt = RandomNumberGenerator.GetBytes(TokenService.SaltSize);

        const int otherIterations = 8192;
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(tokenData, salt, otherIterations, HashAlgorithmName.SHA384, TokenService.HashLength);

        string hash = $"${HashAlgorithmName.SHA384.Name}${otherIterations}${Base64Url.EncodeToString(salt)}${Base64Url.EncodeToString(key)}$";

        // Assert
        _tokenService.VerifyToken(token, hash).Should().BeTrue();
    }
}
