using AwesomeAssertions;

using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The username - what someone signs in with, and what the interface calls them.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>DisplayNameTests</c>, which covered the display-only half of "the username should not
/// be the email address". That half is gone: the account's own <c>UserName</c> now carries the name,
/// so there is one name rather than two and nothing left to seed from an address.
/// </para>
/// <para>
/// The character set is asserted here rather than trusted, because it is the whole reason sign-in can
/// take either identifier in one field without the two namespaces overlapping.
/// </para>
/// </remarks>
public class UsernameTests
{
    /// <summary>
    /// No <c>@</c>, so a username can never be shaped like an address - which is what makes
    /// <c>LoginModel</c>'s username-then-email resolution unambiguous rather than merely ordered.
    /// </summary>
    [Fact]
    public void AUsernameMayNotContainAnAtSign()
    {
        HSUser.AllowedUsernameCharacters.Should().NotContain("@");
    }

    /// <summary>The characters people actually put in a handle are all there.</summary>
    [Theory]
    [InlineData('a')]
    [InlineData('Z')]
    [InlineData('7')]
    [InlineData('-')]
    [InlineData('.')]
    [InlineData('_')]
    public void AUsernameMayContainLettersDigitsAndTheThreePunctuationMarks(char character)
    {
        HSUser.AllowedUsernameCharacters.Should().Contain(character.ToString());
    }

    /// <summary>
    /// The app API's <c>Name</c> is the username. An API called <c>Name</c> should not hand out an
    /// address, and it no longer has to: the account has a name of its own.
    /// </summary>
    [Fact]
    public void TheAppApiReturnsTheUsername()
    {
        HSUser user = new() { UserName = "henrik", Email = "rig@example.com" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("henrik");
    }

    /// <summary>
    /// The fallback exists only because <c>UserName</c> is nullable on the base type. Nothing this
    /// application creates gets there - but a blank name would render as a blank greeting, and the
    /// address is the one other identifier always present.
    /// </summary>
    [Fact]
    public void TheAppApiFallsBackToTheEmailWhenThereIsNoUsername()
    {
        HSUser user = new() { Email = "rig@example.com" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("rig@example.com");
    }
}
