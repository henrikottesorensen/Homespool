using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Take back a <see cref="CancelObject"/>, so the object prints again from the current layer.
/// </summary>
/// <param name="ObjectId">The object's id, 0-based, as <see cref="CancelObject.ObjectId"/>.</param>
public sealed record UncancelObject(int ObjectId) : IPrinterIntent
{
    /// <inheritdoc />
    /// <remarks>The floor only, as for <see cref="CancelObject"/>.</remarks>
    public Capability RequiredCapability => Capability.Print;
}
