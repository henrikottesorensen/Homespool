namespace Homespool.Host.Authentication;

/// <summary>
/// Which flow sent a person to an external provider, carried through the round trip so that the
/// answer is only ever read by the flow that asked for it.
/// </summary>
/// <remarks>
/// <b>Every flow's answer lands in the same external cookie</b>, because the provider has one callback
/// for all of them. Without a name for the flow, a callback that checks only whose account the answer
/// is for cannot tell a link from a re-authentication: a session on a provider-only account could start
/// the re-authentication, which needs no proof, sign in at the provider as somebody else, and open the
/// link callback instead - linking a foreign identity past the proof the link asks for.
/// </remarks>
public enum ExternalRoundTrip
{
    /// <summary>
    /// Nobody said which flow this is. Never started and never read: both ends throw on it, since a
    /// flow nobody named is a programming error rather than an answer to refuse.
    /// </summary>
    Undefined = 0,

    /// <summary>Signing in from the login page, or registering through a provider. No account expects the answer.</summary>
    SignIn = 1,

    /// <summary>Attaching a provider to the signed-in account, on <c>Manage/ExternalLogins</c>.</summary>
    Link = 2,

    /// <summary>A provider-only account proving itself on <c>Account/Reauthenticate</c>.</summary>
    Reauthenticate = 3,
}
