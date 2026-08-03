namespace Homespool.Host.PrintFiles;

/// <summary>What became of a delete.</summary>
public enum PrintFileDeletion
{
    Undefined = 0,

    /// <summary>There was no such file.</summary>
    NotFound,

    /// <summary>The file and its row are gone.</summary>
    Deleted,

    /// <summary>Refused: a queued print still wants this file.</summary>
    Queued,
}
