namespace Homespool.FakePrinter;

/// <summary>
/// What a chunk did to the transfer.
/// </summary>
public enum ChunkOutcome
{
    Undefined = 0,

    /// <summary>Written; the transfer continues and wants another range.</summary>
    Accepted = 1,

    /// <summary>The last byte arrived - the transfer succeeded.</summary>
    Completed = 2,

    /// <summary>
    /// The transfer is dead. Firmware's inline engine has no retry for this
    /// (<c>DownloadStep::FailedRemote</c> goes straight to <c>State::Failed</c>,
    /// transfer.cpp:389-391), so neither does this.
    /// </summary>
    Failed = 3,
}
