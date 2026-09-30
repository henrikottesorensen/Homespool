using Homespool.Model;

namespace Homespool.Host.Printing;

/// <summary>
/// Stop the running print. The acknowledgement means the stop was <i>accepted</i>, not that the
/// print has ended - <see cref="PrintStopService"/> exists because of that gap, and
/// callers wanting attribution go through it rather than sending this directly.
/// </summary>
public sealed record StopPrint : IPrinterIntent
{
    /// <summary>
    /// Whether the stop is made as the printer's operator rather than as the print's owner - set by
    /// <see cref="PrintStopService"/> when <see cref="Capability.ControlPrinter"/> is what allowed it.
    /// </summary>
    /// <remarks>
    /// A flag rather than a capability, so the requirement can only be raised: whatever sets it,
    /// the floor stays <see cref="Capability.Print"/>.
    /// </remarks>
    public bool AsOperator { get; init; }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The floor only.</b> Whether this stop is <i>yours</i> to make is decided by
    /// <see cref="PrintStopService"/>, which every path to a stop goes through;
    /// <see cref="Capability.ControlPrinter"/> stops anybody's.
    /// </para>
    /// <para>
    /// <b>An operator's stop asks for <see cref="Capability.ControlPrinter"/> instead.</b>
    /// <see cref="Capability.ControlPrinter"/> does not imply <see cref="Capability.Print"/>, so a
    /// token scoped to it alone would otherwise be allowed the stop and then refused the send.
    /// </para>
    /// </remarks>
    public Capability RequiredCapability => AsOperator ? Capability.ControlPrinter : Capability.Print;
}
