using System;
using System.Linq;

using AwesomeAssertions;

using Homespool.Host.Localisation;
using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// The token form offers every capability, once.
/// </summary>
/// <remarks>
/// <see cref="CapabilityText.Groups"/> is the only list the minting form draws its boxes from, so a
/// capability missing from it can never be put on a token. Nothing fails when that happens: the enum
/// member compiles, cookie sessions hold it through <see cref="CapabilitySet.Everything"/>, and every
/// token simply lacks it.
/// </remarks>
public sealed class CapabilityTextTests
{
    [Fact]
    public void EveryCapabilityIsOfferedOnTheTokenFormExactlyOnce()
    {
        Capability[] offered = [.. CapabilityText.Groups.SelectMany(group => group.capabilities)];

        offered.Should().OnlyHaveUniqueItems();
        offered.Should().BeEquivalentTo(Enum.GetValues<Capability>().Where(capability => capability.IsSet()),
                                        "a capability with no box can never be granted to a token");
    }
}
