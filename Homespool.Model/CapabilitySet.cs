using System;
using System.Collections;
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
public sealed class CapabilitySet : IReadOnlyCollection<Capability>
{
    /// <summary>Grants nothing. What an empty or absent column parses to.</summary>
    public static readonly CapabilitySet None = new([], []);

    /// <summary>
    /// Grants every capability this build defines - the scope of a credential that narrows nothing,
    /// and the other pole from <see cref="None"/>.
    /// </summary>
    /// <remarks>
    /// <b>Intersecting with this is identity</b>, which is what makes "narrows nothing" expressible
    /// without a null: a credential holding all of them is bounded by its owner's memberships and by
    /// nothing else.
    /// <para>
    /// <b>When stored, it is evaluated at the write, not at the read</b>, and that is the right way
    /// round. A capability added next year does not retroactively widen a credential minted before it
    /// existed - the stored scope names what was granted at the time, and a grant nobody made is not
    /// one to infer. A cookie session, by contrast, is never stored: its <see cref="Caller"/> is built
    /// per request, so it holds whatever this build defines.
    /// </para>
    /// </remarks>
    public static readonly CapabilitySet Everything =
        new([.. Enum.GetValues<Capability>().Where(capability => capability != Capability.Undefined)], []);

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

    /// <summary>How many capabilities this set grants.</summary>
    public int Count => _granted.Count;

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

    /// <summary>
    /// What <paramref name="capability"/> cannot meaningfully be held without, or <c>null</c> where it
    /// stands alone. <b>An act on a resource implies the base view of that resource</b> — you cannot
    /// print on a printer you cannot see, and you cannot judge a camera's configuration without
    /// looking at its picture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Capability.ViewQueue"/> and <see cref="Capability.ViewHistory"/> imply nothing: they
    /// are separate views rather than acts. Nor does any printer capability reach a camera, which is a
    /// different resource.
    /// </para>
    /// <para>
    /// <b>The file capabilities imply nothing either, and that is not the rule failing to reach
    /// them.</b> A printer act implies its view because a printer you cannot see is one you cannot
    /// address - listing filters on it and a lookup answers "no such printer". A file is addressed by
    /// the name the caller already supplies, so deleting one needs no listing. Making
    /// <see cref="Capability.UploadOwnFiles"/> imply <see cref="Capability.ViewOwnFiles"/> would hand a
    /// print-host key the owner's whole library listing for nothing.
    /// </para>
    /// </remarks>
    public static Capability? ImpliedBy(Capability capability)
    {
        return capability switch
        {
            Capability.Print or Capability.ControlPrinter or Capability.ManagePrinter => Capability.ViewPrinter,
            Capability.ManageCamera => Capability.ViewCamera,
            _ => null,
        };
    }

    /// <summary>
    /// The stored form: names separated by single spaces, in enum order so rows compare, <b>with every
    /// implication added</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The closure is applied here, on the way in, and nowhere else.</b> A stored column therefore
    /// carries its own implications and says what it grants — no decoder ring needed by whoever is
    /// reading query output at the wrong end of a bad day. It also makes the incoherent combination
    /// <i>unrepresentable</i> rather than merely rejected: there is no write path that can produce a
    /// <see cref="Capability.Print"/> grant without <see cref="Capability.ViewPrinter"/>, so no
    /// validation rule for a second write path to forget.
    /// </para>
    /// <para>
    /// <b>Deliberately not applied in <see cref="Parse"/>.</b> Closing on the way out would let a row
    /// grant something it does not contain, which is the failing-open direction; a column written
    /// before this rule existed keeps exactly what it says.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><c>Undefined</c> is not a grant and cannot be stored.</exception>
    public static string Format(IEnumerable<Capability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        HashSet<Capability> closed = [.. capabilities];

        if (closed.Contains(Capability.Undefined))
        {
            throw new ArgumentException("Undefined is not a capability and cannot be stored.", nameof(capabilities));
        }

        // A fixpoint rather than one pass. Implications are one level deep today - the base views
        // imply nothing - so a single pass would do; this cannot be wrong the day one of them gains an
        // implication of its own.
        while (true)
        {
            Capability[] additions = closed.Select(ImpliedBy)
                                           .Where(implied => implied is not null && !closed.Contains(implied.Value))
                                           .Select(implied => implied!.Value)
                                           .ToArray();

            if (additions.Length == 0)
            {
                break;
            }

            closed.UnionWith(additions);
        }

        return string.Join(' ', closed.OrderBy(capability => capability));
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

    /// <summary>What this set grants, in the stored spelling.</summary>
    /// <remarks>
    /// <b>Renders what is here, and does not call <see cref="Format"/>.</b> A set that came from
    /// <see cref="Parse"/> need not be closed — a column written before the closure rule, or edited by
    /// hand — and formatting it would show implications this set does not actually grant. A debugging
    /// aid that lies about a permission is worse than none.
    /// </remarks>
    public override string ToString()
    {
        return string.Join(' ', _granted.OrderBy(capability => capability));
    }

    /// <summary>What this set grants, in enum order so enumeration is deterministic.</summary>
    public IEnumerator<Capability> GetEnumerator()
    {
        return _granted.OrderBy(capability => capability).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
