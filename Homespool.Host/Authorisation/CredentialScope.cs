using System;

using Homespool.Host.Exceptions;
using Homespool.Model;

namespace Homespool.Host.Authorisation;

/// <summary>
/// The one credential check: did the key this request arrived on name the capability the action
/// needs? <b>Every gate in the application asks it through here</b> - the printer and camera access
/// services, the file catalog, and the enrolment path.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is shared although the gates around it are not.</b> Nothing here knows about a printer, a
/// camera or a file: the question is about a <see cref="Caller"/> and a <see cref="Capability"/>
/// alone, which is why the file catalog - whose resources have no team and no row to check against -
/// asks it in exactly the same words as a service that resolves a uuid. What differs between the
/// callers is the <i>second</i> question, about a team, and each of them keeps that.
/// </para>
/// <para>
/// <b>The credential's refusal is said out loud, where a team's deliberately is not.</b> The silence
/// stops a uuid being confirmed; a caller's own key leaks nothing about anybody else, so there is
/// nothing to protect by hiding it. It is also the half the caller can act on - a token is theirs to
/// replace, where a membership needs somebody else.
/// </para>
/// <para>
/// <b>Call it first, before any row is looked for.</b> It needs no database, so nothing is saved by
/// deferring it - and raised after a lookup it answers one way for a thing that exists and another
/// for one that does not, which confirms the identifier the silence was meant to keep quiet about.
/// That holds for a team uuid as much as for a camera or printer one. The ordering is the part this
/// class cannot enforce, and the part that has been got wrong separately at every site that once
/// spelled the check itself.
/// </para>
/// </remarks>
public static class CredentialScope
{
    /// <summary>
    /// Refuses when the credential did not name <paramref name="capability"/>.
    /// </summary>
    /// <exception cref="CredentialScopeDeniedException">The credential's scope does not name it.</exception>
    public static void Require(Caller caller, Capability capability)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.Allows(capability))
        {
            throw CredentialScopeDeniedException.For(capability);
        }
    }
}
