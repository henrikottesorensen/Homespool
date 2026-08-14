namespace Homespool.Host.Printing;

/// <summary>
/// Withdraw a <see cref="SetPrinterReady"/> - the printer returns to idle without a person having
/// to clear anything.
/// </summary>
public sealed record CancelPrinterReady : IPrinterIntent;
