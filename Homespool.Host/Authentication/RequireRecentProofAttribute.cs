using System;

namespace Homespool.Host.Authentication;

/// <summary>
/// Requires a live <see cref="RecentProof"/> on the request - the person proved they hold the account
/// within <see cref="RecentProof.Window"/> (or <see cref="MaxAgeSeconds"/>) - sending anyone without
/// one to <c>Account/Reauthenticate</c> first. On a page model it covers every handler; on one handler
/// method it covers that act alone, for a page where one button among many is the one that wants a
/// fresh proof.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared on the page rather than applied to a folder</b>, for the reason <c>Program</c> gives
/// for declining an <c>AuthorizeFolder</c> convention: somebody auditing one page should see what
/// protects it by looking at it. The attribute is metadata; <see cref="RecentProofPageFilter"/> is
/// what acts on it, and <c>RecentProofDeclarationTests</c> is what stops a page in a folder that wants
/// the proof from shipping with neither this nor an explicit <see cref="NoRecentProofAttribute"/>.
/// </para>
/// <para>
/// <b>It runs after authorisation, never instead of it.</b> Authorisation filters run first, so an
/// anonymous visitor is at the login page and a signed-in non-administrator is refused before this is
/// reached. A page that needs a role still says so with <c>[Authorize(Roles = …)]</c> beside this: the
/// proof page proves a credential, not a role.
/// </para>
/// <para>
/// <b>A refused POST is not replayed after the proof.</b> The redirect goes to the page's own path, so
/// an act attempted on a stale tab has to be asked for again deliberately. Re-running a destructive
/// act automatically once a password arrives is how a walked-away browser turns into a surprise.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireRecentProofAttribute : Attribute
{
    /// <summary>
    /// A shorter window than <see cref="RecentProof.Window"/> for this page or act, in seconds. Zero,
    /// the default, means the shared window.
    /// </summary>
    public int MaxAgeSeconds { get; init; }

    /// <summary>The window this declaration asks for.</summary>
    public TimeSpan MaxAge => MaxAgeSeconds > 0 ? TimeSpan.FromSeconds(MaxAgeSeconds) : RecentProof.Window;
}
