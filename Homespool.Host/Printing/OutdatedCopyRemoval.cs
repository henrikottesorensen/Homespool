namespace Homespool.Host.Printing;

/// <summary>What became of an older version of a file on a printer's drive before a newer one is sent.</summary>
public enum OutdatedCopyRemoval
{
    Undefined = 0,

    /// <summary>
    /// Nothing to remove: no copy under the name, the current version, or bytes Homespool cannot
    /// vouch for and so does not delete.
    /// </summary>
    NothingToRemove = 1,

    /// <summary>The older copy is gone - deleted now, or already gone when asked.</summary>
    Removed = 2,

    /// <summary>The printer is using the older copy - printing it, or still receiving it - and kept it.</summary>
    InUse = 3,

    /// <summary>The printer would not delete it, for a reason that is not the copy being in use.</summary>
    Refused = 4,
}
