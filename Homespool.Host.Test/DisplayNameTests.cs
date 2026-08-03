using AwesomeAssertions;

using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="HSUser.DisplayName"/> - what the interface calls someone, as distinct from
/// <c>UserName</c>, which stays the email address and stays the sign-in identifier.
/// </summary>
/// <remarks>
/// Added with the feature, because nothing covered the behaviour it changed: <see cref="UserReadDTO"/>
/// went from returning the email to preferring the display name and the whole suite stayed green,
/// which means that field's value had never been asserted anywhere.
/// </remarks>
public class DisplayNameTests
{
    /// <summary>A new account is called by the local part of its address, not the whole thing.</summary>
    [Theory]
    [InlineData("rig@example.com", "rig")]
    [InlineData("henrik.sorensen@example.com", "henrik.sorensen")]
    [InlineData("no-at-sign", "no-at-sign")]
    public void ADisplayNameIsSeededFromTheEmailsLocalPart(string email, string expected)
    {
        HSUser.DefaultDisplayNameFor(email).Should().Be(expected);
    }

    /// <summary>
    /// Null rather than an empty string when there is nothing to use, so the fallback chain runs
    /// instead of the interface rendering an empty greeting.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@example.com")]
    public void AnEmailWithNoLocalPartSeedsNothing(string? email)
    {
        HSUser.DefaultDisplayNameFor(email).Should().BeNull();
    }

    /// <summary>An address whose local part exceeds the column is truncated, not rejected.</summary>
    [Fact]
    public void AnOverlongLocalPartIsTruncatedToTheColumnLength()
    {
        string local = new('a', HSUser.DisplayNameMaxLength + 20);

        HSUser.DefaultDisplayNameFor(local + "@example.com")
              .Should().HaveLength(HSUser.DisplayNameMaxLength);
    }

    /// <summary>
    /// The app API's <c>Name</c> prefers the display name - the point of the field. An API called
    /// <c>Name</c> should not hand out an address just because that is what people sign in with.
    /// </summary>
    [Fact]
    public void TheAppApiPrefersTheDisplayName()
    {
        HSUser user = new() { UserName = "rig@example.com", Email = "rig@example.com", DisplayName = "Henrik" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("Henrik");
    }

    /// <summary>
    /// An account created before this existed has no display name, and still reads sensibly rather
    /// than blank.
    /// </summary>
    [Fact]
    public void TheAppApiFallsBackToTheSignInNameWhenThereIsNoDisplayName()
    {
        HSUser user = new() { UserName = "rig@example.com", Email = "rig@example.com" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("rig@example.com");
    }
}
