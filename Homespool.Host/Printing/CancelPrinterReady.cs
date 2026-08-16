using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Withdraw a <see cref="SetPrinterReady"/> - the printer returns to idle without a person having
/// to clear anything.
/// </summary>
public sealed record CancelPrinterReady : IPrinterIntent
{
    /// <inheritdoc />
    /// <remarks>
    /// Withdrawing an assertion you were entitled to make, so it matches
    /// <see cref="SetPrinterReady"/>. Harmless and self-correcting either way: nothing is destroyed,
    /// the queue simply waits, and anyone holding <see cref="Capability.Print"/> can ready it again.
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;
}
