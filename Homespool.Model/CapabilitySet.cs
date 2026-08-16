using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Homespool.Model;

/// <summary>
/// A parsed <c>TeamMember.Capabilities</c> column: the <see cref="Capability"/> values it grants, and
/// any names in it that this build does not recognise.
/// </summary>
/// <remarks>
/// <para>
/// <b>The column is written and read only through here.</b> A hand-edited row is the one way an
/// unknown name can appear — that, or a rename of a <see cref="Capability"/> member whose data
/// migration was missed — and both are worth someone knowing about.
/// </para>
/// <para>
/// <b>An unrecognised name grants nothing</b>, so the failure direction is closed. It is also silent,
/// which is why it is carried in <see cref="Unrecognised"/> rather than dropped: the caller logs it.
/// Parsing deliberately does not throw — one bad row must not take out every permission check in the
/// application, and refusing is already the safe answer.
/// </para>
/// </remarks>
public sealed class CapabilitySet
{
    /// <summary>Grants nothing. What an empty or absent column parses to.</summary>
    public static readonly CapabilitySet None = new([], []);

    private readonly ImmutableHashSet<Capability> _granted;

    private CapabilitySet(ImmutableHashSet<Capability> granted, ImmutableArray<string> unrecognised)
    {
        _granted = granted;
        Unrecognised = unrecognised;
    }

    /// <summary>
    /// Names found in the column that are not <see cref="Capability"/> values in this build. Empty in
    /// every ordinary case.
    /// </summary>
    public ImmutableArray<string> Unrecognised { get; }

    /// <summary>The capabilities this set grants, for display and for intersecting.</summary>
    public IReadOnlySet<Capability> Granted => _granted;

    /// <summary>
    /// Reads a stored column. Whitespace-separated, order-insensitive, duplicate-tolerant;
    /// <c>null</c>, empty and whitespace all give <see cref="None"/>.
    /// </summary>
    /// <remarks>
    /// <c>Undefined</c> is refused by name like any other unknown: it is the "nobody said" value, so a
    /// column containing it is a bug rather than a grant of nothing.
    /// </remarks>
    public static CapabilitySet Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return None;
        }

        ImmutableHashSet<Capability>.Builder granted = ImmutableHashSet.CreateBuilder<Capability>();
        ImmutableArray<string>.Builder unrecognised = ImmutableArray.CreateBuilder<string>();

        foreach (string name in stored.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries
                                                            | StringSplitOptions.TrimEntries))
        {
            // Case-sensitive on purpose: the writer is this class, so a difference in case means
            // something else wrote the column, which is exactly what Unrecognised exists to report.
            if (Enum.TryParse(name, out Capability capability) && capability != Capability.Undefined
                && Enum.IsDefined(capability))
            {
                granted.Add(capability);
            }
            else
            {
                unrecognised.Add(name);
            }
        }

        return new(granted.ToImmutable(), unrecognised.ToImmutable());
    }

    /// <summary>The stored form: names separated by single spaces, in enum order so rows compare.</summary>
    /// <exception cref="ArgumentException"><c>Undefined</c> is not a grant and cannot be stored.</exception>
    public static string Format(IEnumerable<Capability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        Capability[] ordered = capabilities.Distinct().OrderBy(capability => capability).ToArray();

        if (Array.IndexOf(ordered, Capability.Undefined) >= 0)
        {
            throw new ArgumentException("Undefined is not a capability and cannot be stored.", nameof(capabilities));
        }

        return string.Join(' ', ordered);
    }

    /// <summary>Whether this set grants <paramref name="capability"/>.</summary>
    /// <remarks>
    /// <b><see cref="Capability.Undefined"/> throws rather than answering false.</b> Nothing
    /// legitimately asks whether a set grants "nobody said", so reaching here with one is an
    /// uninitialised field, a deserialised zero or a forgotten argument - a programming error, not a
    /// refusal. Answering false would be safe and silent, which is how it would survive to production.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is Undefined.</exception>
    public bool Allows(Capability capability)
    {
        if (capability == Capability.Undefined)
        {
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Undefined is not a capability.");
        }

        return _granted.Contains(capability);
    }

    /// <summary>
    /// The capabilities present in both sets. <b>The shape a token scope will need</b>: a scope may
    /// only ever narrow what its holder's membership already allows, never widen it, and an
    /// intersection is the only operation that cannot get that wrong.
    /// </summary>
    public CapabilitySet Intersect(CapabilitySet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new(_granted.Intersect(other._granted), []);
    }

    /// <summary>The stored form of this set.</summary>
    public override string ToString()
    {
        return Format(_granted);
    }
}
