using System;

using AwesomeAssertions;

using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="Caller"/> - who is asking, and what the credential they arrived on lets them ask for.
/// </summary>
/// <remarks>
/// The class is small enough to look obvious, which is the reason to pin it: every permission decision
/// in the application passes through <see cref="Caller.Allows"/>, and the failure that matters here is
/// silent in the granting direction.
/// </remarks>
public class CallerTests
{
    /// <summary>
    /// <b>An unscoped credential narrows nothing.</b> It is not "grants everything" - the access
    /// services still ask the membership - it is "adds no second restriction", which is what keeps a
    /// browser session and the queue loop behaving exactly as they did before scopes existed.
    /// </summary>
    [Theory]
    [InlineData(Capability.ViewPrinter)]
    [InlineData(Capability.Print)]
    [InlineData(Capability.ControlPrinter)]
    [InlineData(Capability.ManagePrinter)]
    [InlineData(Capability.ManageCamera)]
    public void AnUnscopedCallerNarrowsNothing(Capability capability)
    {
        // Act
        Caller caller = Caller.Unscoped(7);

        // Assert
        caller.UserId.Should().Be(7);
        caller.IsScoped.Should().BeFalse();
        caller.Allows(capability).Should().BeTrue("an unscoped credential adds no restriction of its own");
    }

    /// <summary>A scoped credential permits what it names, and refuses what it does not.</summary>
    [Fact]
    public void AScopedCallerAllowsOnlyWhatItNames()
    {
        // Arrange
        Caller caller = Caller.Scoped(7, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

        // Assert
        caller.IsScoped.Should().BeTrue();
        caller.Allows(Capability.Print).Should().BeTrue();
        caller.Allows(Capability.ViewPrinter)
              .Should()
              .BeTrue("Print implies ViewPrinter, and the closure is applied when the scope is written");
        caller.Allows(Capability.ControlPrinter)
              .Should()
              .BeFalse("the scope never named it, so the credential cannot ask for it");
    }

    /// <summary>
    /// <b>A scope cannot widen.</b> Naming a capability the owner's membership never held buys nothing:
    /// the access services intersect, so this side only ever removes. Pinned here because a reading of
    /// <see cref="Caller"/> alone might suggest a scope grants.
    /// </summary>
    [Fact]
    public void AScopeNamingSomethingTheMembershipLacksGrantsNothing()
    {
        // Arrange
        CapabilitySet membership = CapabilitySet.Parse(CapabilitySet.Format(CapabilityPresets.Contributor));
        Caller caller = Caller.Scoped(7, CapabilitySet.Parse(CapabilitySet.Format([Capability.ManagePrinter])));

        // Act - what an access service computes
        bool permitted = membership.Allows(Capability.ManagePrinter) && caller.Allows(Capability.ManagePrinter);

        // Assert
        caller.Allows(Capability.ManagePrinter).Should().BeTrue("the credential named it");
        permitted.Should().BeFalse("but a Contributor never held it, and a scope only ever narrows");
    }

    /// <summary>An empty scope is a real state, and it refuses everything rather than defaulting open.</summary>
    [Fact]
    public void AnEmptyScopeRefusesEverything()
    {
        // Arrange
        Caller caller = Caller.Scoped(7, CapabilitySet.None);

        // Assert
        caller.IsScoped.Should().BeTrue("empty is a scope, not the absence of one");
        caller.Allows(Capability.ViewPrinter).Should().BeFalse();
        caller.Allows(Capability.Print).Should().BeFalse();
    }

    /// <summary>A scoped caller needs a scope; the null is not a second spelling of unscoped.</summary>
    [Fact]
    public void ScopedRefusesANullScope()
    {
        FluentActions.Invoking(() => Caller.Scoped(7, null!))
                     .Should()
                     .Throw<ArgumentNullException>("Unscoped is how a caller says it narrows nothing");
    }
}
