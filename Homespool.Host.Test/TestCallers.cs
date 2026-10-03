using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.Test;

/// <summary>
/// Callers for fixtures, holding the scope a token minted with exactly these capabilities would hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drive the operation under test with its least scope, not with <see cref="Caller.Unscoped"/>.</b>
/// An unscoped caller passes every scope check, so a branch that asks for more than the operation's
/// documented minimum stays green under it - the shape of a slicer key refused by a path its own
/// tests never ran as. Narrowing the membership does not cover it: the file capabilities belong to no
/// membership, and the scope and team checks are separate code, so one can be forgotten while the
/// other holds.
/// </para>
/// <para>
/// <b>The set is closed the way minting closes it</b>, so <c>Print</c> carries <c>ViewPrinter</c> here
/// as it does on a real token. A test that needs an unclosed set is testing the closure, and builds
/// the set itself.
/// </para>
/// </remarks>
internal static class TestCallers
{
    public static Caller Scoped(long userId, params IEnumerable<Capability> scope)
    {
        return Caller.Scoped(userId, CapabilitySet.Parse(CapabilitySet.Format(scope)));
    }
}
