using System;

using AwesomeAssertions;

using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="CapabilitySet"/> - the only reader and writer of <c>TeamMember.Capabilities</c>.
/// </summary>
/// <remarks>
/// Worth its own class rather than being implied by the services above it: every permission decision
/// in the application is one <see cref="CapabilitySet.Allows"/> call, so a defect here is a defect
/// everywhere, and the interesting cases are all about malformed or hostile column content that no
/// service test would produce.
/// </remarks>
public class CapabilitySetTests
{
    /// <summary>
    /// <b>The trap that decided against matching this column in SQL.</b> <c>ViewPrinter</c> contains
    /// the string <c>Print</c>, so the padded <c>LIKE</c> that a string column invites - and any
    /// <c>Contains</c> written in a hurry - grants <see cref="Capability.Print"/> to a viewer. This is
    /// not hypothetical about some future capability; it is true of the vocabulary as it stands.
    /// </summary>
    [Fact]
    public void ACapabilityWhoseNameSitsInsideAnothersIsNotGrantedByIt()
    {
        // Arrange
        CapabilitySet viewer = CapabilitySet.Parse(nameof(Capability.ViewPrinter));

        // Act & Assert
        viewer.Allows(Capability.ViewPrinter).Should().BeTrue();
        viewer.Allows(Capability.Print)
              .Should()
              .BeFalse("'ViewPrinter' contains 'Print', and a substring match would hand over the printer");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentColumnGrantsNothing(string? stored)
    {
        // Act
        CapabilitySet capabilities = CapabilitySet.Parse(stored);

        // Assert
        capabilities.Granted.Should().BeEmpty();
        capabilities.Allows(Capability.ViewPrinter).Should().BeFalse();
    }

    /// <summary>Order, spacing and repetition are the column's business, not the reader's.</summary>
    [Fact]
    public void ParsingIsOrderInsensitiveAndTolerantOfRepeatsAndExtraSpace()
    {
        // Act
        CapabilitySet capabilities = CapabilitySet.Parse("  Print   ViewPrinter\tPrint  ");

        // Assert
        capabilities.Granted.Should().BeEquivalentTo([Capability.Print, Capability.ViewPrinter]);
        capabilities.Unrecognised.Should().BeEmpty();
    }

    [Fact]
    public void WhatIsWrittenIsWhatIsRead()
    {
        // Arrange
        string stored = CapabilitySet.Format(CapabilityPresets.Operator);

        // Act
        CapabilitySet capabilities = CapabilitySet.Parse(stored);

        // Assert
        capabilities.Granted.Should().BeEquivalentTo(CapabilityPresets.Operator);
    }

    /// <summary>
    /// A name this build does not know grants nothing and is <i>reported</i> - the silent half is what
    /// <see cref="CapabilitySet.Unrecognised"/> exists to make visible.
    /// </summary>
    [Fact]
    public void AnUnrecognisedNameGrantsNothingAndIsCarriedOut()
    {
        // Act
        CapabilitySet capabilities = CapabilitySet.Parse("ViewPrinter ViewPrintr RunTheWholeFactory");

        // Assert
        capabilities.Allows(Capability.ViewPrinter).Should().BeTrue("the recognised name still counts");
        capabilities.Unrecognised.Should().BeEquivalentTo(["ViewPrintr", "RunTheWholeFactory"]);
    }

    /// <summary>Case is not normalised, because this class is the only thing that should be writing.</summary>
    [Fact]
    public void AMisCasedNameIsUnrecognisedRatherThanAccepted()
    {
        // Act
        CapabilitySet capabilities = CapabilitySet.Parse("viewprinter");

        // Assert
        capabilities.Allows(Capability.ViewPrinter).Should().BeFalse();
        capabilities.Unrecognised.Should().BeEquivalentTo(["viewprinter"]);
    }

    /// <summary>
    /// <c>Undefined</c> is the "nobody said" value, so it is refused at every door: it cannot be
    /// stored, a column carrying it does not grant it, and asking about it is a programming error
    /// rather than a question with a false answer.
    /// </summary>
    [Fact]
    public void UndefinedIsRefusedEveryWayItCanArrive()
    {
        // Act & Assert
        FluentActions.Invoking(() => CapabilitySet.Format([Capability.Print, Capability.Undefined]))
                     .Should().Throw<ArgumentException>();

        CapabilitySet.Parse("Undefined").Unrecognised.Should().BeEquivalentTo(["Undefined"]);

        FluentActions.Invoking(() => CapabilitySet.Parse("Print").Allows(Capability.Undefined))
                     .Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// <b>The property a token scope will rest on.</b> An intersection cannot produce a capability
    /// neither side held, so a scope can only ever narrow the membership behind it.
    /// </summary>
    [Fact]
    public void IntersectingNarrowsAndCannotWiden()
    {
        // Arrange
        CapabilitySet membership = CapabilitySet.Parse(CapabilitySet.Format(CapabilityPresets.Operator));
        CapabilitySet scope = CapabilitySet.Parse("ViewPrinter Print ManagePrinter");

        // Act
        CapabilitySet effective = membership.Intersect(scope);

        // Assert
        effective.Granted.Should().BeEquivalentTo([Capability.ViewPrinter, Capability.Print]);
        effective.Allows(Capability.ManagePrinter)
                 .Should()
                 .BeFalse("the scope named it but the membership never had it");
        effective.Allows(Capability.ControlPrinter)
                 .Should()
                 .BeFalse("the membership had it but the scope did not name it");
    }
}
