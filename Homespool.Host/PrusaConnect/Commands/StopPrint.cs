using Homespool.Model;

namespace Homespool.Host.PrusaConnect.Commands;

public class StopPrint : ISendableCommand
{
    public string WireName => "STOP_PRINT";

    /// <inheritdoc />
    /// <remarks>The floor only. Whether this stop is <i>yours</i> to make is decided by <c>PrintStopService</c>, which every stop goes through; <see cref="Capability.ControlPrinter"/> stops anyone's.</remarks>
    public Capability RequiredCapability => Capability.Print;
}
