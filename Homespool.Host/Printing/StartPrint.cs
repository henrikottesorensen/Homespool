namespace Homespool.Host.Printing;

/// <summary>
/// Start printing a file already on the printer's own storage, named by the path the printer
/// knows it by. Getting the file there first is transfer machinery and deliberately not an
/// intent yet - it is per-protocol from end to end.
/// </summary>
public sealed record StartPrint(string Path) : IPrinterIntent;
