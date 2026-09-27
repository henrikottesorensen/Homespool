using System.Collections.Generic;

using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Takes back a <see cref="CancelObject"/> - <c>UNCANCEL_OBJECT</c>, the same kwarg and the same
/// handler with the flag the other way round (<c>command.cpp:185</c>, <c>planner.cpp:1071</c> at
/// <c>v6.10.1</c>).
/// </summary>
/// <remarks>
/// Answered exactly as <see cref="CancelObject"/> is, with the whole cancelled set. It only brings back
/// what has not been passed: layers skipped while the object was cancelled are not printed later.
/// </remarks>
public class UncancelObject : ISendableCommand
{
    /// <summary>The object's id, 0-based, as <see cref="CancelObject.Id"/>.</summary>
    public ushort Id { get; set; }

    public string WireName => "UNCANCEL_OBJECT";

    /// <summary>The one kwarg, typed as <see cref="CancelObject.Arguments"/> is.</summary>
    public IReadOnlyDictionary<string, object?> Arguments => new Dictionary<string, object?>
    {
        ["id"] = Id,
    };

    /// <inheritdoc />
    /// <remarks>The floor only, as for <see cref="CancelObject"/>.</remarks>
    public Capability RequiredCapability => Capability.Print;
}
