using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Localization;

using Homespool.Model;

namespace Homespool.Host.Localisation;

/// <summary>
/// What a <see cref="Capability"/> is called where a person has to choose one, and which group it is
/// shown under.
/// </summary>
/// <remarks>
/// <para>
/// <b>The enum name is not the label.</b> <c>ManipulateOwnFiles</c> says what the code means and
/// nothing a person would recognise; the label says <i>rename and delete files</i>. The names are the
/// stored vocabulary and must not drift toward prose, so the translation lives here instead.
/// </para>
/// <para>
/// <b>One place, because two would disagree.</b> The minting form and the token listing both name
/// capabilities, and a token whose scope reads differently from the boxes that created it is worse
/// than one that says nothing at all.
/// </para>
/// </remarks>
public class CapabilityText
{
    private readonly IStringLocalizer<SharedResource> _localiser;

    public CapabilityText(IStringLocalizer<SharedResource> localiser)
    {
        _localiser = localiser;
    }

    /// <summary>
    /// The capabilities a person may put on a token, in the order they are shown, grouped by the thing
    /// they act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ordered by resource rather than by strength</b>, because that is how somebody deciding thinks
    /// about it - what may this key touch, then how far. Within a group the reads come first.
    /// </para>
    /// <para>
    /// <b>The headings reuse the pages' own title keys</b> rather than minting <c>Capability_Group_*</c>
    /// duplicates. Three new keys carrying the three words the pages already say is exactly the
    /// duplication <c>NoTwoKeysCarryTheSameEnglish</c> exists to catch, and it caught them.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string groupKey, IReadOnlyList<Capability> capabilities)> Groups { get; } =
    [
        ("Printers_Title",
        [
            Capability.ViewPrinter,
            Capability.ViewQueue,
            Capability.ViewHistory,
            Capability.Print,
            Capability.ControlPrinter,
            Capability.ManagePrinter,
        ]),
        ("Cameras_Title", [Capability.ViewCamera, Capability.ManageCamera]),
        ("Files_Title",
        [
            Capability.ViewOwnFiles,
            Capability.UploadOwnFiles,
            Capability.ManipulateOwnFiles,
        ]),
    ];

    /// <summary>What this capability is called, in the reader's language.</summary>
    public string For(Capability capability)
    {
        return _localiser["Capability_" + capability];
    }

    /// <summary>A group's heading, in the reader's language.</summary>
    public string Group(string groupKey)
    {
        return _localiser[groupKey];
    }

    /// <summary>
    /// A stored scope said as a sentence fragment - the labels, comma-separated, in the order the form
    /// shows them.
    /// </summary>
    /// <remarks>
    /// <b>Reads the set rather than the string</b>, so a scope written before a capability was renamed
    /// still lists what it does grant and silently omits what this build cannot name. Empty means the
    /// token can do nothing, which is worth saying rather than rendering as a blank cell.
    /// </remarks>
    public string Describe(string? storedScope)
    {
        CapabilitySet scope = CapabilitySet.Parse(storedScope);

        if (scope.Granted.Count == 0)
        {
            return _localiser["Capability_Nothing"];
        }

        if (CapabilitySet.Everything.All(scope.Allows))
        {
            return _localiser["Capability_Everything"];
        }

        return string.Join(
            _localiser["Common_ListSeparator"],
            Groups.SelectMany(group => group.capabilities)
                  .Where(scope.Allows)
                  .Select(For));
    }
}
