namespace Homespool.Host.Authorisation;

/// <summary>
/// What an account might want to do to a camera, in operations rather than in permission flags.
/// </summary>
/// <remarks>
/// Separate from <see cref="PrinterOperation"/> because a camera is not a printer: it can exist
/// without one, and the operations it has are not a subset of a printer's. Mapping both onto one
/// enum would mean naming printer operations that cannot apply here.
/// </remarks>
public enum CameraOperation
{
    /// <summary>
    /// Unset. Reserved so that <c>default(CameraOperation)</c> is not silently a real permission -
    /// see <c>notes/housekeeping.md</c>, where CA1008 was satisfied by a zero member that granted
    /// the most permissive read.
    /// </summary>
    Undefined = 0,

    /// <summary>See the camera and its picture. Requires <c>CanRead</c> on its team.</summary>
    ViewCamera,

    /// <summary>
    /// Add, change or remove a camera. Requires <c>CanManage</c> on its team.
    /// </summary>
    /// <remarks>
    /// <c>CanManage</c> rather than <c>CanUse</c> because a camera's address is fetched by the
    /// server: whoever sets it chooses what this deployment reaches on their behalf, which is an
    /// administrative act rather than an operational one.
    /// </remarks>
    ManageCamera,
}
