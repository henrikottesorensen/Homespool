namespace Homespool.Host.Printing;

/// <summary>
/// Return the printer to idle from a finished or stopped screen - clearing the end-of-print state
/// remotely, which is distinct from <see cref="CancelPrinterReady"/> (that undoes a readiness
/// assertion; this dismisses a completed job's screen).
/// </summary>
public sealed record SetPrinterIdle : IPrinterIntent;
