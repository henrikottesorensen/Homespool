using System;

using AwesomeAssertions;

using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="EnumValues"/>: a value is set when it is a named member and not the reserved zero.
/// </summary>
/// <remarks>
/// <b>Both halves are pinned separately</b>, because each alone is a plausible implementation:
/// <c>Enum.IsDefined</c> by itself answers true for <c>Undefined</c>, and a comparison with the
/// default by itself answers true for a number no member carries.
/// </remarks>
public class EnumValuesTests
{
    [Fact]
    public void TheReservedZeroIsNotSet()
    {
        Capability.Undefined.IsSet().Should().BeFalse("Undefined is a defined member, and still nobody set it");
        default(PrinterStatus).IsSet().Should().BeFalse();
    }

    [Fact]
    public void ANumberNoMemberCarriesIsNotSet()
    {
        ((Capability)9999).IsSet().Should().BeFalse("it is not the default, and it is not a member either");
    }

    [Fact]
    public void ANamedMemberIsSet()
    {
        Capability.Print.IsSet().Should().BeTrue();
    }

    [Fact]
    public void RequireSetHandsBackAValueThatIsSet()
    {
        Capability.Print.RequireSet().Should().Be(Capability.Print);
    }

    [Fact]
    public void RequireSetNamesTheArgumentItRefuses()
    {
        Capability wanted = Capability.Undefined;

        Action guard = () => wanted.RequireSet();

        guard.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("wanted");
    }

    /// <summary>
    /// The permission check refuses a number no capability carries as loudly as it refuses Undefined:
    /// both are a value nobody legitimately asks about.
    /// </summary>
    [Fact]
    public void ACapabilitySetWillNotBeAskedAboutACapabilityNobodySet()
    {
        CapabilitySet everything = CapabilitySet.Everything;

        Action undefined = () => everything.Allows(Capability.Undefined);
        Action unknown = () => everything.Allows((Capability)9999);

        undefined.Should().Throw<ArgumentOutOfRangeException>();
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Nor stored: a number no capability carries would reach the column as digits and come back unrecognised.</summary>
    [Fact]
    public void ACapabilityNobodySetIsNotStored()
    {
        Action unknown = () => CapabilitySet.Format([Capability.Print, (Capability)9999]);

        unknown.Should().Throw<ArgumentException>();
    }
}
