using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Stops printing one object of the running print and carries on with the rest -
/// <c>CANCEL_OBJECT</c>, one kwarg (<c>command.cpp:184</c>, <c>:408</c> at <c>v6.10.1</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Answered with a <c>CANCELABLE_CHANGED</c>, never with <c>FINISHED</c></b>: firmware confirms by
/// sending the whole cancelled set under this command's id, even when nothing changed because the
/// object was already cancelled (<c>planner.cpp:826-847</c>). So it is idempotent and always
/// answers, and the answer is the new state rather than the old - the handler runs synchronously.
/// </para>
/// <para>
/// <b>Refused on a build without the feature</b> - <c>REJECTED</c>, "Not supported on this printer
/// type", which <c>M486.cpp</c> says is the iX. That arrives as an ordinary refusal.
/// </para>
/// <para>
/// <b><see cref="Id"/> is the object's index as the slicer declared it, from zero.</b> The printer's
/// own menu numbers the same objects from one; carrying a number off that screen into this field
/// cancels the neighbouring object and raises no error.
/// </para>
/// </remarks>
public class CancelObject : ISendableCommand
{
    /// <summary>The object's id, 0-based. Firmware parses it as a <c>uint16_t</c>.</summary>
    public ushort Id { get; set; }

    public string WireName => "CANCEL_OBJECT";

    /// <summary>The one kwarg. A <see cref="ushort"/>, because firmware rejects a mismatched type rather than coercing it.</summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["id"] = Id,
    };

    /// <inheritdoc />
    /// <remarks>The floor only. That the print is <i>yours</i> is decided by <c>PrintObjectService</c>, which every cancel goes through.</remarks>
    public Capability RequiredCapability => Capability.Print;
}
