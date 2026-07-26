namespace PrinterService.Host.PrusaConnect.Commands;

/// <summary>
/// An <see cref="ICommand"/> the server actually knows how to send, as opposed to the ~25 other
/// concrete classes in this folder that are still hollow markers awaiting the rest of the command
/// vocabulary. Narrower than <see cref="ICommand"/> on purpose, so those still-hollow classes don't
/// need a throwaway <see cref="WireName"/> implementation.
/// </summary>
public interface ISendableCommand : ICommand
{
    /// <summary>The wire string for a J-command's JSON payload. Confirmed against firmware source
    /// (Prusa-Firmware-Buddy command.cpp:149-166 at e96ce2b, the ref AGENT-NOTES.md pins).</summary>
    string WireName { get; }
}
