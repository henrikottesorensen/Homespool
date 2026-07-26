namespace Homespool.FakePrinter;

/// <summary>
/// Outcome of parsing one server frame: exactly one of <see cref="Frame"/> (well-formed) or
/// <see cref="Broken"/> (the rejection the firmware would plan) is non-null.
/// </summary>
/// <param name="Frame">The parsed frame, when the header was well-formed.</param>
/// <param name="Broken">The firmware-faithful rejection, when it was not.</param>
public sealed record FrameParseResult(ServerCommandFrame? Frame, BrokenFrame? Broken);
