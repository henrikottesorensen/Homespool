using System;

using Microsoft.EntityFrameworkCore;

namespace Homespool.Host.Accounts;

/// <summary>
/// Refuses a write that must not ride in the caller's transaction: counting a failed attempt, or
/// starting a cooldown.
/// </summary>
/// <remarks>
/// A count written inside a transaction is undone by its rollback, so a wrong guess that also failed
/// the surrounding work would cost nothing. Throwing makes the mistake loud where silently enlisting
/// would make it free. Giving an attempt back and clearing a count may enlist: a rollback of those
/// leaves the count standing, which is the direction to fail in.
/// </remarks>
internal static class OutsideTransaction
{
    public static void Require(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A failed attempt or a cooldown is written on its own, never inside a transaction: a rollback would undo it.");
        }
    }
}
