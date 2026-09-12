using System;

namespace Homespool.Host.Authentication;

/// <summary>
/// Marks a page in a folder whose pages are expected to carry <see cref="RequireRecentProofAttribute"/>
/// that deliberately does <b>not</b> - a page that only reads, or the proof page itself, which would
/// otherwise redirect to itself for ever.
/// </summary>
/// <remarks>
/// It does nothing at run time. Its whole job is to make the exemption a decision somebody wrote
/// down, so that <c>RecentProofDeclarationTests</c> can tell a page that was thought about from a page
/// that was forgotten - the same reason <c>[AllowAnonymous]</c> is spelled out on the pages that are
/// anonymous on purpose.
/// </remarks>
/// <param name="reason">Why this page is reachable without a recent proof.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NoRecentProofAttribute(string reason) : Attribute
{
    /// <summary>Why this page is reachable without a recent proof.</summary>
    public string Reason { get; } = reason;
}
