namespace Homespool.Host.Queue;

/// <summary>The entry at the front of a printer's queue, with what is known about its file.</summary>
/// <param name="QueuedPrintId">The queue entry.</param>
/// <param name="PrintFileId">The file it wants.</param>
/// <param name="FileName">Its name, for logs and for matching the printer's <c>FILE_INFO</c>.</param>
/// <param name="FileHasArrived">Whether the bytes are believed to be on the drive.</param>
/// <param name="PrinterPath">What the printer calls it, once a <c>FILE_INFO</c> has said.</param>
public sealed record QueueHead(long QueuedPrintId,
                               long PrintFileId,
                               string FileName,
                               bool FileHasArrived,
                               string? PrinterPath);
