using System.Globalization;

using Microsoft.AspNetCore.Identity;

namespace Homespool.Host.Services;

/// <summary>
/// Corrects the one Identity message that this application makes untrue.
/// </summary>
/// <remarks>
/// <para>
/// <c>InvalidUserName</c>'s default reads "can only contain letters or digits", which is Identity
/// describing a rule it does not itself apply - the allowed set is
/// <c>IdentityOptions.User.AllowedUserNameCharacters</c>, and ours (
/// <see cref="Model.Entities.HSUser.AllowedUsernameCharacters"/>) includes three punctuation marks
/// the message denies. Somebody told "letters or digits" after typing <c>henrik.sorensen</c> would
/// go and remove the dot that was never the problem.
/// </para>
/// <para>
/// <b>Registered in <c>Program</c> only, not in the test harness</b> - unlike
/// <see cref="IdentityConfiguration"/>, which both share because it decides what is *accepted*. This
/// only decides what a rejection *says*, so a test seeing Identity's wording instead is a difference
/// with nothing behind it.
/// </para>
/// </remarks>
public class HSIdentityErrorDescriber : IdentityErrorDescriber
{
    /// <inheritdoc/>
    public override IdentityError InvalidUserName(string? userName)
    {
        return new IdentityError
        {
            Code = nameof(InvalidUserName),
            Description = string.Format(
                CultureInfo.CurrentCulture,
                "'{0}' is not a usable username. Use letters, digits, and - . _ only; an email address cannot be a username.",
                userName),
        };
    }
}
