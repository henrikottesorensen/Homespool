using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Model.Entities;

namespace Homespool.Host.Authorisation;

/// <summary>
/// May this account do this to this camera - the camera half of the question
/// <see cref="PrinterAccessService"/> answers for printers.
/// </summary>
/// <remarks>
/// <para>
/// <b>A camera's authority comes from its team, not from its printer.</b> A camera may be bound to
/// a printer or not, and inheriting permission through the binding would mean an unbound camera had
/// no owner at all - so the rule would work for the common case and quietly fail for the other one.
/// <see cref="Camera.TeamId"/> is required for exactly this reason.
/// </para>
/// <para>
/// <b>Two entry points rather than three</b>, unlike the printer gate. There is no path here that
/// arrives with an id something else already resolved: cameras are reached by UUID from a page or a
/// route, so the "safe to distinguish missing from forbidden" case does not arise, and offering it
/// would invite somebody to use it where it is not safe.
/// </para>
/// </remarks>
public class CameraAccessService
{
    private readonly HomespoolDbContext _dbContext;

    public CameraAccessService(HomespoolDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// The permission an operation needs.
    /// </summary>
    public static Func<TeamMember, bool> RequiredPermission(CameraOperation operation)
    {
        return operation switch
        {
            CameraOperation.ViewCamera => member => member.CanRead,
            CameraOperation.ManageCamera => member => member.CanManage,

            // A new operation with no mapping is a programming error, and defaulting would grant
            // rather than refuse.
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation), operation, "No permission is mapped to this operation."),
        };
    }

    /// <summary>
    /// Every camera this account may see, newest last.
    /// </summary>
    public async Task<IReadOnlyList<Camera>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Cameras
                               .Where(camera => _dbContext.TeamMembers.Any(member => member.TeamId == camera.TeamId &&
                                                                                     member.UserId == userId && member.CanRead))
                               .Include(camera => camera.Printer)
                               .OrderBy(camera => camera.Name ?? string.Empty)
                               .ThenBy(camera => camera.Id)
                               .AsNoTracking()
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    /// <summary>
    /// The cameras watching a printer that this account may see.
    /// </summary>
    /// <remarks>
    /// Scoped by the camera's own team rather than the printer's. They are expected to match, but
    /// authority over a camera comes from the camera - inheriting it through the printer is the rule
    /// that works for bound cameras and quietly fails for unbound ones.
    /// </remarks>
    public async Task<IReadOnlyList<Camera>> ListForPrinterAsync(int printerId, long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Cameras
                               .Where(camera => camera.PrinterId == printerId)
                               .Where(camera => _dbContext.TeamMembers.Any(member => member.TeamId == camera.TeamId &&
                                                                                     member.UserId == userId && member.CanRead))
                               .OrderBy(camera => camera.Name ?? string.Empty)
                               .ThenBy(camera => camera.Id)
                               .AsNoTracking()
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    /// <summary>
    /// The camera, or <see langword="null"/> if it does not exist <i>or</i> this account may not
    /// touch it.
    /// </summary>
    /// <remarks>
    /// The two are deliberately indistinguishable, following <see cref="PrinterAccessService"/>: a
    /// UUID that answers differently for "no such camera" and "not yours" is a way to find out which
    /// cameras exist.
    /// </remarks>
    public async Task<Camera?> FindAsync(Guid uuid, long userId, CameraOperation operation, CancellationToken cancellationToken)
    {
        Camera? camera = await _dbContext.Cameras
                                         .Include(entity => entity.Printer)
                                         .FirstOrDefaultAsync(entity => entity.Uuid == uuid, cancellationToken)
                                         .ConfigureAwait(false);

        if (camera is null)
        {
            return null;
        }

        TeamMember? membership = await _dbContext.TeamMembers
                                                 .FirstOrDefaultAsync(
                                                     member => member.TeamId == camera.TeamId && member.UserId == userId,
                                                     cancellationToken)
                                                 .ConfigureAwait(false);

        if (membership is null || !RequiredPermission(operation)(membership))
        {
            return null;
        }

        return camera;
    }

    /// <summary>
    /// Whether this account is an administrator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answered here, from the role tables by id, rather than by reading a
    /// <c>ClaimsPrincipal</c> at the edge and passing a flag in. These services take a user id on
    /// purpose - see <see cref="PrinterAccessService"/> - and the argument that keeps the gate
    /// inside them applies with more force to a check a caller could simply forget to make.
    /// </para>
    /// <para>
    /// Used for one thing: claiming a camera plugged into this machine. That is a property of the
    /// server rather than of a team, in the same way the printer certificate is, and
    /// <c>Pages/Admin/Certificate</c> is the precedent it follows.
    /// </para>
    /// </remarks>
    public async Task<bool> IsAdministratorAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.UserRoles
                               .Join(
                                   _dbContext.Roles,
                                   userRole => userRole.RoleId,
                                   role => role.Id,
                                   (userRole, role) => new { userRole.UserId, role.Name })
                               .AnyAsync(
                                   pair => pair.UserId == userId && pair.Name == Services.AdminBootstrap.AdminRole,
                                   cancellationToken)
                               .ConfigureAwait(false);
    }

    /// <summary>
    /// The teams this account may add a camera to.
    /// </summary>
    /// <remarks>
    /// Needed because creating a camera has no camera to check yet - the question is about a team
    /// rather than a row, which is the one shape <see cref="FindAsync"/> cannot express.
    /// </remarks>
    public async Task<IReadOnlyList<Team>> ManageableTeamsAsync(long userId, CancellationToken cancellationToken)
    {
        return await _dbContext.TeamMembers
                               .Where(member => member.UserId == userId && member.CanManage)
                               .Select(member => member.Team!)
                               .OrderBy(team => team.Name ?? string.Empty)
                               .ThenBy(team => team.Id)
                               .AsNoTracking()
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }
}
