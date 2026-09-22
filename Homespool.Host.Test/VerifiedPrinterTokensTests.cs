using System;

using AwesomeAssertions;

using Microsoft.Extensions.Time.Testing;

using Homespool.Host.Authentication;

namespace Homespool.Host.Test;

/// <summary>
/// The memo on its own: what it answers for a pair it has and has not seen, and that it forgets.
/// </summary>
public sealed class VerifiedPrinterTokensTests
{
    private const string StoredHash = "$SHA384$4096$c2FsdA$aGFzaA$";

    private const string Token = "abcdefghijklmnopqrst";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [Fact]
    public void ARememberedPairIsVerified()
    {
        // Arrange
        VerifiedPrinterTokens memo = new(_time);

        // Act
        memo.Remember(StoredHash, Token);

        // Assert
        memo.IsVerified(StoredHash, Token).Should().BeTrue();
    }

    [Fact]
    public void AnotherTokenAgainstARememberedHashIsNotVerified()
    {
        // Arrange
        VerifiedPrinterTokens memo = new(_time);
        memo.Remember(StoredHash, Token);

        // Act
        bool verified = memo.IsVerified(StoredHash, "tsrqponmlkjihgfedcba");

        // Assert
        verified.Should().BeFalse();
    }

    [Fact]
    public void TheSameTokenAgainstAnotherHashIsNotVerified()
    {
        // Arrange
        VerifiedPrinterTokens memo = new(_time);
        memo.Remember(StoredHash, Token);

        // Act
        bool verified = memo.IsVerified("$SHA384$4096$b3RoZXI$aGFzaA$", Token);

        // Assert
        verified.Should().BeFalse();
    }

    [Fact]
    public void AVerificationIsTrustedUntilJustBeforeItsLifetimeEnds()
    {
        // Arrange
        VerifiedPrinterTokens memo = new(_time);
        memo.Remember(StoredHash, Token);

        // Act
        _time.Advance(VerifiedPrinterTokens.Lifetime - TimeSpan.FromSeconds(1));

        // Assert
        memo.IsVerified(StoredHash, Token).Should().BeTrue();
    }

    [Fact]
    public void AnExpiredVerificationIsForgottenWhenAskedAbout()
    {
        // Arrange
        VerifiedPrinterTokens memo = new(_time);
        memo.Remember(StoredHash, Token);
        _time.Advance(VerifiedPrinterTokens.Lifetime);

        // Act
        bool verified = memo.IsVerified(StoredHash, Token);

        // Assert
        verified.Should().BeFalse();
        memo.Count.Should().Be(0);
    }

    /// <summary>
    /// An entry nobody asks about again - a superseded hash after a rebind - is swept on the way into a
    /// later remember, so the memo does not keep one per credential ever issued.
    /// </summary>
    [Fact]
    public void ExpiredEntriesNobodyAsksAboutAreSwept()
    {
        // Arrange
        VerifiedPrinterTokens memo = new(_time);
        memo.Remember(StoredHash, Token);
        _time.Advance(VerifiedPrinterTokens.Lifetime);

        // Act
        memo.Remember("$SHA384$4096$b3RoZXI$aGFzaA$", Token);

        // Assert
        memo.Count.Should().Be(1);
    }
}
