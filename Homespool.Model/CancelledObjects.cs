using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Homespool.Model.Entities;

namespace Homespool.Model;

/// <summary>
/// The stored form of <see cref="PrinterLiveState.CancelledObjectIds"/>: ascending object ids joined
/// by commas, <c>"2,4"</c>, and the empty string for none.
/// </summary>
/// <remarks>
/// <para>
/// <b>A string rather than a child table</b> because the set is replaced whole on every report and
/// read whole on every render - it is a value, and a table would add a join and a delete-and-reinsert
/// to what is one assignment. Ascending so that two equal sets are one string, which is what lets a
/// polled fragment render identically when nothing changed.
/// </para>
/// <para>
/// <b>Parsing is forgiving and formatting is strict.</b> The column is only ever written through
/// <see cref="Format"/>, so a value <see cref="Parse"/> cannot read did not come from here; it drops
/// what it cannot read rather than throwing on a page render.
/// </para>
/// </remarks>
public static class CancelledObjects
{
    /// <summary>The stored form of <paramref name="ids"/>.</summary>
    public static string Format(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        return string.Join(',', ids.Distinct()
                                   .Order()
                                   .Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The ids in <paramref name="stored"/>; empty for null or empty.</summary>
    public static IReadOnlySet<int> Parse(string? stored)
    {
        HashSet<int> ids = [];

        if (string.IsNullOrEmpty(stored))
        {
            return ids;
        }

        foreach (string part in stored.Split(','))
        {
            if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
