namespace Homespool.Host.Telemetry;

/// <summary>
/// What a directory listing said about a printer's drive - the whole of it, since the next listing
/// replaces this one rather than adding to it.
/// </summary>
/// <param name="FileCount">The wire's own <c>file_count</c>.</param>
/// <param name="Entries">
/// The wire's <c>children</c> array as raw JSON, or null where the listing carried a count and no
/// entries.
/// </param>
public sealed record PrinterDriveListingUpdate(int FileCount, string? Entries);
