using System;
using System.Collections.Generic;
using System.Linq;

using Homespool.Model;

namespace Homespool.Host.Notifications;

/// <summary>
/// Reads and writes <c>HSUser.MutedNotifications</c>: the kinds a person has turned off, as
/// space-separated <see cref="NotificationKind"/> names.
/// </summary>
/// <remarks>
/// A name nobody recognises is skipped rather than refused. The column is written only by this class,
/// so an unknown name is a member that has since been removed, and it should stop meaning anything
/// rather than stop the account from being read.
/// </remarks>
public static class NotificationMutes
{
    /// <summary>Every kind a person can turn off, in the order the settings page lists them.</summary>
    public static readonly IReadOnlyList<NotificationKind> Choosable =
    [
        NotificationKind.PrinterNeedsAttention,
        NotificationKind.PrintFinished,
        NotificationKind.PrintDidNotFinish,
        NotificationKind.QueueHeld,
    ];

    /// <summary>The kinds turned off in <paramref name="stored"/>.</summary>
    public static IReadOnlySet<NotificationKind> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new HashSet<NotificationKind>();
        }

        return stored.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .Select(name => Enum.TryParse(name, ignoreCase: false, out NotificationKind kind) ? kind : NotificationKind.Undefined)
                     .Where(kind => kind.IsSet())
                     .ToHashSet();
    }

    /// <summary>What to store for <paramref name="muted"/>, or null when nothing is turned off.</summary>
    public static string? Format(IEnumerable<NotificationKind> muted)
    {
        ArgumentNullException.ThrowIfNull(muted);

        string[] names = [.. muted.Where(kind => kind.IsSet()).Distinct().Order().Select(kind => kind.ToString())];

        return names.Length == 0 ? null : string.Join(' ', names);
    }
}
