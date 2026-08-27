using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;

namespace Homespool.Host.Services;

/// <summary>
/// Corrects the one Identity message that this application makes untrue, in the reader's language.
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
/// <para>
/// <b>Every other message is still Identity's own, and still English.</b> Overriding one member to
/// read from resources does not localise the base class: a wrong password and a too-short one are
/// answered by <c>IdentityErrorDescriber</c> in whatever language it shipped in. Localising those
/// means overriding each in turn, which is deliberately not smuggled in here.
/// </para>
/// </remarks>
public class HSIdentityErrorDescriber : IdentityErrorDescriber
{
    private readonly IStringLocalizer<SharedResource> _localiser;

    public HSIdentityErrorDescriber(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <inheritdoc/>
    public override IdentityError InvalidUserName(string? userName)
    {
        // The localiser formats with CurrentCulture, which the request-localisation middleware has
        // already set - so the message arrives in the language the page is being rendered in rather
        // than the server's.
        return new IdentityError
        {
            Code = nameof(InvalidUserName),
            Description = _localiser["Account_InvalidUserName", userName ?? string.Empty],
        };
    }
}
