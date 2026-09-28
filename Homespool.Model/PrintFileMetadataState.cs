namespace Homespool.Model;

/// <summary>
/// How much a print file's row knows about what the file was sliced for.
/// </summary>
/// <remarks>
/// <para>
/// <b>The distinction that earns this column is <see cref="Silent"/> against
/// <see cref="Unreadable"/>.</b> A file carrying no slicer configuration is the ordinary shape of
/// output from anything that is not PrusaSlicer, and a file that could not be parsed is damage.
/// Both leave every other column null, so without this they are the same row - and a compatibility
/// check that cannot tell them apart either says nothing about corrupt files or cries wolf about
/// half the world's slicers.
/// </para>
/// <para>
/// <b>It also separates both from <see cref="Unread"/></b>: a row indexed without its bytes being
/// read, or one whose bytes changed after they were. The reconciler's background pass reads those,
/// as it fills in a missing <c>PrintFile.Digest</c>, and treats <see cref="Undefined"/> - rows written
/// before this existed - the same way. Until then an unread row's columns are empty, so a
/// compatibility check has nothing to say rather than something stale.
/// </para>
/// <para>
/// Stored as text, following <c>PrintHoldReason</c> and <c>PrintJob.State</c>.
/// </para>
/// </remarks>
public enum PrintFileMetadataState
{
    /// <summary>Not a state. Present so a default-valued row is not silently a real answer.</summary>
    Undefined = 0,

    /// <summary>Nobody has looked at these bytes yet; the reconciler's background pass will.</summary>
    Unread = 1,

    /// <summary>Looked, and the file is not one this can parse. Corruption, or a container nobody knows.</summary>
    Unreadable = 2,

    /// <summary>Read cleanly, and it carried no slicer configuration at all.</summary>
    Silent = 3,

    /// <summary>Read cleanly, and the columns beside this one carry what it said.</summary>
    Read = 4,
}
