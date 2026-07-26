using System;

namespace Homespool.Host.Services;

/// <summary>
/// Invitation configuration, bound from the <c>Invitations</c> configuration section.
/// </summary>
/// <remarks>
/// Only the lifetime is configurable for now (AGENT-NOTES phase-1.5 §15 decision 5). An admin can
/// still override the expiry per invite at creation time; this is the default when they don't.
/// </remarks>
public class InvitationOptions
{
    public const string SectionName = "Invitations";

    /// <summary>How long a new invite stays valid, in hours. 48 by default.</summary>
    public int LifetimeHours { get; set; } = 48;

    /// <summary><see cref="LifetimeHours"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Lifetime => TimeSpan.FromHours(LifetimeHours);
}
