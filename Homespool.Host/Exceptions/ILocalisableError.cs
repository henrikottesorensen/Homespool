namespace Homespool.Host.Exceptions;

/// <summary>
/// An exception whose message a page is expected to show somebody, and which therefore has to be
/// sayable in their language.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key travels; the sentence does not.</b> These types are thrown deep in services that have
/// no request, no culture and no business acquiring a localiser — <c>UserFileStore</c> should not
/// take a dependency on the resource system to refuse a file name. So the throw site states
/// <i>which</i> sentence and <i>what goes in it</i>, and whoever renders it decides in what language.
/// </para>
/// <para>
/// <b><see cref="System.Exception.Message"/> stays English on purpose, and is not a fallback for a
/// missing key.</b> It is what reaches the log, the stack trace and the developer reading either,
/// and those should not move with whoever happened to trigger the fault. The two texts say the same
/// thing in two places for two audiences, which is the one duplication here worth having.
/// </para>
/// <para>
/// Found late: exception messages were a user-facing surface for as long as this application has had
/// pages, and three separate audits missed them because they searched for literals *in* page models
/// and services rather than in the types those files throw. See <c>notes/localisation.md</c>.
/// </para>
/// </remarks>
public interface ILocalisableError
{
    /// <summary>The resource key for the sentence a reader should see.</summary>
    string ResourceKey { get; }

    /// <summary>
    /// What fills that sentence's holes.
    /// </summary>
    /// <remarks>
    /// May legitimately contain text this application did not write — a printer's own refusal
    /// reason, or a file name somebody typed. Those stay as they are; only the sentence around them
    /// is translated.
    /// </remarks>
    object[] ResourceArguments { get; }
}
