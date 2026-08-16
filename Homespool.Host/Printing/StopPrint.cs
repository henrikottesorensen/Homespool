using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Stop the running print. The acknowledgement means the stop was <i>accepted</i>, not that the
/// print has ended - <see cref="Services.PrintStopService"/> exists because of that gap, and
/// callers wanting attribution go through it rather than sending this directly.
/// </summary>
public sealed record StopPrint : IPrinterIntent
{
    /// <inheritdoc />
    /// <remarks>
    /// <b>The floor only.</b> Whether this stop is <i>yours</i> to make is decided by
    /// <see cref="Services.PrintStopService"/>, which every path to a stop goes through;
    /// <see cref="Capability.ControlPrinter"/> stops anybody's.
    /// </remarks>
    public Capability RequiredCapability => Capability.Print;
}
