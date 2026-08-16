using System.Security.Claims;

using AwesomeAssertions;

using Homespool.Host.Authorisation;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CallerResolver"/> - the one place a request becomes a <see cref="Caller"/>.
/// </summary>
/// <remarks>
/// Worth pinning even while no handler writes a scope claim: the failure this guards is that the
/// resolver stops reading the principal and hardcodes an answer, which would leave every token
/// unscoped the day scopes start being issued - silent, and in the granting direction.
/// </remarks>
public class CallerResolverTests
{
    /// <summary>
    /// A sign-in cookie carries no scope claim, so it narrows nothing. The ordinary case today, and
    /// the only case a browser can produce.
    /// </summary>
    [Fact]
    public void APrincipalWithNoScopeClaimIsUnscoped()
    {
        // Arrange
        ClaimsPrincipal principal = new(new ClaimsIdentity());

        // Act
        Caller caller = CallerResolver.For(9, principal);

        // Assert
        caller.UserId.Should().Be(9);
        caller.IsScoped.Should().BeFalse();
        caller.Allows(Capability.ManagePrinter).Should().BeTrue("nothing narrowed it");
    }

    /// <summary>The scope is read from the claim rather than assumed.</summary>
    [Fact]
    public void AScopeClaimIsReadIntoTheCaller()
    {
        // Arrange
        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(HSClaimTypes.Scope, CapabilitySet.Format([Capability.Print])),
        ]));

        // Act
        Caller caller = CallerResolver.For(9, principal);

        // Assert
        caller.IsScoped.Should().BeTrue();
        caller.Allows(Capability.Print).Should().BeTrue();
        caller.Allows(Capability.ViewPrinter).Should().BeTrue("the closure travelled with it");
        caller.Allows(Capability.ControlPrinter).Should().BeFalse("the claim never named it");
    }

    /// <summary>
    /// <b>Absent and empty are different.</b> No claim means the credential named no subset; an empty
    /// claim is a scope that grants nothing, which somebody can deliberately mint. Collapsing them
    /// would make a powerless token indistinguishable from a browser session - in the wrong direction.
    /// </summary>
    [Fact]
    public void AnEmptyScopeClaimGrantsNothingRatherThanEverything()
    {
        // Arrange
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(HSClaimTypes.Scope, string.Empty)]));

        // Act
        Caller caller = CallerResolver.For(9, principal);

        // Assert
        caller.IsScoped.Should().BeTrue();
        caller.Allows(Capability.ViewPrinter).Should().BeFalse();
        caller.Allows(Capability.Print).Should().BeFalse();
    }

    /// <summary>
    /// The queue loop has no request to read, so it says so at the call site rather than passing a
    /// principal that would not be there.
    /// </summary>
    [Fact]
    public void ForUserIdIsUnscopedByConstruction()
    {
        CallerResolver.ForUserId(9).IsScoped.Should().BeFalse();
    }
}
