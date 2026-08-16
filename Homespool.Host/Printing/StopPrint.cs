namespace Homespool.Host.Printing;

/// <summary>
/// Stop the running print. The acknowledgement means the stop was <i>accepted</i>, not that the
/// print has ended - <see cref="Services.PrintStopService"/> exists because of that gap, and
/// callers wanting attribution go through it rather than sending this directly.
/// </summary>
public sealed record StopPrint : IPrinterIntent;
