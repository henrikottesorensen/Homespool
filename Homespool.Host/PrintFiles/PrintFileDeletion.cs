namespace Homespool.Host.PrintFiles;

/// <summary>What became of a delete.</summary>
public enum PrintFileDeletion
{
    Undefined = 0,

    /// <summary>There was no such file.</summary>
    NotFound = 1,

    /// <summary>The file and its row are gone.</summary>
    Deleted = 2,

    /// <summary>Refused: a queued print still wants this file.</summary>
    Queued = 3,
}
