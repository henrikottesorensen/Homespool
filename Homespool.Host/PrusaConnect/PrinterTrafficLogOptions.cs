namespace Homespool.Host.PrusaConnect;

/// <summary>
/// The printer traffic log: whether it runs and where it writes, bound from the
/// <c>PrinterTrafficLog</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b>Global rather than per-printer.</b> A printer misbehaving is usually not a printer anybody has
/// identified yet - the deployments this exists for have one or two printers, and asking which one
/// to watch is asking the question the log is meant to answer.
/// </para>
/// <para>
/// <b>Read once, at startup.</b> Turning this on means restarting, because the file handle is opened
/// when the log is constructed. That is the same restart the minimum log level already needs, and it
/// is why <see cref="Telemetry"/> exists as its own switch: the expensive half can be left off
/// without a second decision about the cheap half.
/// </para>
/// </remarks>
public class PrinterTrafficLogOptions
{
    public const string SectionName = "PrinterTrafficLog";

    /// <summary>
    /// Whether to write the traffic log at all. Off by default: this records message bodies, which
    /// the ordinary log deliberately does not.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether to record telemetry messages as well as events and commands. Off even when
    /// <see cref="Enabled"/> is on.
    /// </summary>
    /// <remarks>
    /// Telemetry arrives at roughly 1 Hz per printer and is almost all of the volume, while events
    /// and commands are the half that explains a misbehaving printer: what was asked for, what came
    /// back, and in which order. Turn this on when the question is specifically about what telemetry
    /// did or did not report - a state that was never sampled, say - and expect the file to grow by
    /// orders of magnitude. The appliance writes to an SD card.
    /// </remarks>
    public bool Telemetry { get; set; }

    /// <summary>
    /// Where the log is written. Relative paths resolve against the content root, and the date is
    /// inserted before the extension as each day rolls over.
    /// </summary>
    /// <remarks>
    /// Under <c>data/</c> because that is what <c>compose.yaml</c> mounts as a volume - a log written
    /// anywhere else vanishes with the container, which is exactly when somebody wants to read it.
    /// </remarks>
    public string Path { get; set; } = "data/traffic/printer-traffic-.jsonl";
}
