namespace Homespool.Host.Printing;

/// <summary>
/// Mark the printer ready for the next job - a person's assertion that the bed is clear, never
/// inferred (<see cref="Model.PrinterStatus.Ready"/>). On Prusa Connect the printer owns this
/// state, so the intent has a wire command; a protocol without the concept has it owned by
/// Homespool instead, and the intent would not reach a wire at all.
/// </summary>
public sealed record SetPrinterReady : IPrinterIntent;
