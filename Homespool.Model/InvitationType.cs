namespace Homespool.Model;

/// <summary>
/// What redeeming an <c>Invitation</c> does: create an account, or give an existing one back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stored as text</b>, like every enum column here. It sits beside
/// <c>Invitation.RecoversUserId</c>, which stays because it is what a recovery is bound to, so the
/// two can disagree; a row where they do is refused by <c>InvitationService</c> rather than read
/// either way.
/// </para>
/// <para>
/// <b>The two types share a token, a lifetime and a link</b>, so nothing a redeeming page receives
/// tells them apart; only the row does. That is why <c>InvitationService</c>'s lookups take the
/// types a caller accepts rather than returning whatever the token opens: a page that only creates
/// accounts must not be handed a recovery and treat it as permission to make one.
/// </para>
/// </remarks>
public enum InvitationType
{
    /// <summary>Not a type. No invitation reports it.</summary>
    Undefined = 0,

    /// <summary>Creates an account, optionally joining a team.</summary>
    Signup = 1,

    /// <summary>
    /// Gives an existing account, named by id, back to an owner who has lost their credentials.
    /// </summary>
    Recovery = 2,
}
