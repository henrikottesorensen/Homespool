namespace Homespool.Model;

/// <summary>
/// What a notification is about - the unit a person turns notifications on and off by.
/// </summary>
/// <remarks>
/// Stored by name in <see cref="Entities.HSUser.MutedNotifications"/>, so a member may be added
/// anywhere and none may be renamed: a renamed member would silently turn back on for everyone who had
/// turned it off.
/// </remarks>
public enum NotificationKind
{
    /// <summary>Never set. The zero value every enum here reserves for "nobody wrote this".</summary>
    Undefined = 0,

    /// <summary>
    /// A printer has stopped to wait for a person - a dialog, a filament change, a red error screen.
    /// </summary>
    PrinterNeedsAttention = 1,

    /// <summary>A print finished.</summary>
    PrintFinished = 2,

    /// <summary>A print ended without finishing: stopped, or failed.</summary>
    PrintDidNotFinish = 3,

    /// <summary>A printer's queue is held behind something a person has to sort out.</summary>
    QueueHeld = 4,

    /// <summary>A print will stop for a filament change within a few minutes.</summary>
    FilamentChangeSoon = 5,

    /// <summary>A printer stopped answering while it had a print running.</summary>
    PrinterLost = 6,

    /// <summary>
    /// Homespool itself became unhealthy, or recovered. Administrators only; turning it off silences
    /// their browsers, and leaves the alert email to go out where a mail server is configured.
    /// </summary>
    ServiceHealth = 7,
}
