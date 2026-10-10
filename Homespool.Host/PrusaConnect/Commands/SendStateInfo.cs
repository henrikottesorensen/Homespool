using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

/// <summary>
/// Asks the printer what state it is in now. Firmware answers with a <c>STATE_CHANGED</c> event carrying
/// the same <c>command_id</c> (<c>planner.cpp:967-969</c>), whose state is read into
/// <see cref="Printing.CommandOutcome.PrinterStatus"/> - the printer's own word, a round trip old, where
/// telemetry may be seconds old.
/// </summary>
public class SendStateInfo : ISendableCommand
{
    /// <inheritdoc />
    public string WireName => "SEND_STATE_INFO";

    /// <inheritdoc />
    public Capability RequiredCapability => Capability.ViewPrinter;
}
