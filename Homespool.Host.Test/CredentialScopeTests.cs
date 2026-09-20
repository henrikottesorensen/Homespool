using AwesomeAssertions;

using Homespool.Host.Authorisation;
using Homespool.Host.Exceptions;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CredentialScope"/> - the one credential check the printer and camera gates, the file
/// catalog and the enrolment path all call.
/// </summary>
/// <remarks>
/// Its callers' own suites cover it where it stands in each of them; these cases are about the shared
/// method's own contract, which is now a public one with four call sites.
/// </remarks>
public sealed class CredentialScopeTests
{
    private const long Alice = 1;

    /// <summary>
    /// <b>The refusal names the capability</b>, because the holder of a narrowed token is the one
    /// person who can act on knowing which box to tick - which is the whole reason a scope refusal is
    /// spoken where a team refusal is silent.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheCapabilityTheCredentialLeftOut()
    {
        // Arrange
        Caller printing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

        // Act & Assert
        FluentActions.Invoking(() => CredentialScope.Require(printing, Capability.ManageCamera))
                     .Should()
                     .Throw<CredentialScopeDeniedException>()
                     .WithMessage($"*{nameof(Capability.ManageCamera)}*");
    }

    /// <summary>
    /// A credential that named the capability passes, and so does one that narrowed nothing - the
    /// browser session every page arrives on.
    /// </summary>
    [Fact]
    public void ACredentialThatNamesItIsNotRefused()
    {
        // Arrange
        Caller printing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.Print])));

        // Act & Assert
        FluentActions.Invoking(() => CredentialScope.Require(printing, Capability.Print))
                     .Should()
                     .NotThrow();

        FluentActions.Invoking(() => CredentialScope.Require(Caller.Unscoped(Alice), Capability.ManageCamera))
                     .Should()
                     .NotThrow("a browser session narrows nothing");
    }

    /// <summary>
    /// <b>The implied capability counts.</b> A scope naming <c>ManageCamera</c> carries
    /// <c>ViewCamera</c>, because the closure is applied when a scope is written rather than when it
    /// is read - so the check needs no implication logic of its own.
    /// </summary>
    [Fact]
    public void AnImpliedCapabilityPassesToo()
    {
        // Arrange
        Caller managing = Caller.Scoped(Alice, CapabilitySet.Parse(CapabilitySet.Format([Capability.ManageCamera])));

        // Act & Assert
        FluentActions.Invoking(() => CredentialScope.Require(managing, Capability.ViewCamera))
                     .Should()
                     .NotThrow();
    }
}
