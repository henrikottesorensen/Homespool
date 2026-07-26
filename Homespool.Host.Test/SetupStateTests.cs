using System;

using AwesomeAssertions;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// Covers the first-run bootstrap secret: the check that gates <c>/setup</c> and the one-way close
/// that stops a second administrator being minted. These are the security-critical bits of step 4,
/// otherwise only exercised by hand.
/// </summary>
public class SetupStateTests
{
    /// <summary>
    /// A base64 string of the right length (24 bytes) but not the real secret. Stands in for a caller
    /// guessing a well-formed token.
    /// </summary>
    private static readonly string WrongButWellFormedToken = Convert.ToBase64String(new byte[24]);

    /// <summary>
    /// Before <see cref="SetupState.Initialize"/> there is no secret, so nothing verifies. Guards
    /// against a gate that would treat "not yet seeded" as "anything goes".
    /// </summary>
    [Fact]
    public void VerifyReturnsFalseBeforeInitialization()
    {
        // Arrange
        SetupState state = new();

        // Assert
        state.IsComplete.Should().BeFalse();
        state.Verify(WrongButWellFormedToken).Should().BeFalse();
    }

    /// <summary>
    /// The token handed back by <see cref="SetupState.Initialize"/> is the one that verifies. This is
    /// the happy path the operator walks: copy the logged token into <c>/setup</c>.
    /// </summary>
    [Fact]
    public void CorrectTokenVerifiesWhileSetupIsPending()
    {
        // Arrange
        SetupState state = new();
        string? token = state.Initialize(adminExists: false);

        // Assert
        token.Should().NotBeNull();
        state.Verify(token).Should().BeTrue();
    }

    /// <summary>
    /// A well-formed token that is not the secret is rejected - the constant-time comparison is
    /// against the real value, not merely a shape check.
    /// </summary>
    [Fact]
    public void WrongTokenIsRejected()
    {
        // Arrange
        SetupState state = new();
        string? token = state.Initialize(adminExists: false);

        // Assert
        token.Should().NotBe(WrongButWellFormedToken);
        state.Verify(WrongButWellFormedToken).Should().BeFalse();
    }

    /// <summary>
    /// Absent, empty, malformed and wrong-length candidates are all rejected without throwing - a
    /// garbage token must fail exactly like a wrong one, never surface a parse exception to the caller.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not valid base64 !!!")]
    [InlineData("YWJj")] // valid base64, but 3 bytes - wrong length for the 24-byte secret
    public void MalformedOrWrongLengthTokensAreRejected(string? candidate)
    {
        // Arrange
        SetupState state = new();
        state.Initialize(adminExists: false);

        // Act
        Func<bool> verify = () => state.Verify(candidate);

        // Assert
        verify.Should().NotThrow();
        verify().Should().BeFalse();
    }

    /// <summary>
    /// When an administrator already exists, initialization mints no token and the state reports
    /// complete, so <c>/setup</c> is closed from the first request of the process.
    /// </summary>
    [Fact]
    public void InitializationWithAnExistingAdminYieldsNoTokenAndAClosedState()
    {
        // Arrange
        SetupState state = new();

        // Act
        string? token = state.Initialize(adminExists: true);

        // Assert
        token.Should().BeNull();
        state.IsComplete.Should().BeTrue();
        state.Verify(WrongButWellFormedToken).Should().BeFalse();
    }

    /// <summary>
    /// The core anti-replay guarantee: once setup completes, even the token that was valid a moment
    /// ago stops verifying. This is what prevents the bootstrap token from being reused to create a
    /// second administrator after the first is set up.
    /// </summary>
    [Fact]
    public void CompletingSetupClosesVerificationEvenForThePreviouslyValidToken()
    {
        // Arrange
        SetupState state = new();
        string? token = state.Initialize(adminExists: false);
        state.Verify(token).Should().BeTrue("the token is valid right up until setup completes");

        // Act
        state.MarkComplete();

        // Assert
        state.IsComplete.Should().BeTrue();
        state.Verify(token).Should().BeFalse();
    }

    /// <summary>
    /// Each initialization draws a fresh secret, so a token from an earlier process (e.g. logged before
    /// a restart) does not verify against a later one.
    /// </summary>
    [Fact]
    public void ReinitializationMintsADifferentSecret()
    {
        // Arrange
        SetupState first = new();
        SetupState second = new();

        // Act
        string? firstToken = first.Initialize(adminExists: false);
        string? secondToken = second.Initialize(adminExists: false);

        // Assert
        secondToken.Should().NotBe(firstToken);
        second.Verify(firstToken).Should().BeFalse();
    }
}
