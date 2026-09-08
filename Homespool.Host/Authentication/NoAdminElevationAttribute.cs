using System;

namespace Homespool.Host.Authentication;

/// <summary>
/// Marks a page under <c>/Admin</c> that deliberately does <b>not</b> require
/// <see cref="AdminElevation"/> — the challenge page itself, which is where an elevation is earned
/// and would otherwise redirect to itself for ever.
/// </summary>
/// <remarks>
/// It does nothing at run time. Its whole job is to make the exemption a decision somebody wrote
/// down, so that <c>AdminElevationDeclarationTests</c> can tell a page that was thought about from a
/// page that was forgotten — the same reason <c>[AllowAnonymous]</c> is spelled out on the seven
/// pages that are anonymous on purpose.
/// </remarks>
/// <param name="reason">Why this page is reachable without an elevation.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NoAdminElevationAttribute(string reason) : Attribute
{
    /// <summary>Why this page is reachable without an elevation.</summary>
    public string Reason { get; } = reason;
}
