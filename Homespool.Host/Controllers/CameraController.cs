using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.Controllers;

/// <summary>
/// Serving camera pictures. <c>/api/v1/cameras</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing may reach the stream server except through here.</b> Its API has no authorisation of
/// its own - the credential added 2026-08-09 is one password for everything, with no notion of which
/// cameras a caller may see - and <c>frame.jpeg</c> takes a stream name as a query parameter. So a
/// browser is never pointed at the sidecar: knowing a camera's identifier would otherwise be the
/// whole of the access control.
/// </para>
/// <para>
/// <b>A request is also what schedules the next capture.</b> Frames are fetched only while somebody
/// is looking, so this endpoint being called is the signal that somebody is - see
/// <see cref="CameraFrameCache"/>. Nothing polls in the background, and a camera nobody has open
/// costs nothing.
/// </para>
/// </remarks>
[ApiController]
[Route("/api/v1/cameras")]
[Authorize(Policy = Policies.Api)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public class CameraController : ControllerBase
{
    private readonly CameraAccessService _access;
    private readonly CameraFrameCache _frames;
    private readonly Go2RtcClient _streamServer;
    private readonly UserManager<HSUser> _userManager;

    public CameraController(
        CameraAccessService access,
        CameraFrameCache frames,
        Go2RtcClient streamServer,
        UserManager<HSUser> userManager)
    {
        _access = access;
        _frames = frames;
        _streamServer = streamServer;
        _userManager = userManager;
    }

    /// <summary>
    /// The camera's current picture. <c>GET /api/v1/cameras/{uuid}/frame</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Answers 204 rather than a stale picture.</b> A frame past its maximum age is discarded by
    /// the cache, so "nothing current" is a real answer and the caller shows that it is capturing. A
    /// day-old photograph of a clear print bed looks exactly like a current one, which is the whole
    /// reason this is not allowed to serve one.
    /// </para>
    /// <para>
    /// The first request after a quiet period will usually be a 204: it starts the capture rather
    /// than waiting for it, so a slow camera cannot make a page slow. The next poll has the picture.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Route("{uuid:guid}/frame")]
    [Produces("image/jpeg")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Frame(Guid uuid, CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return Forbid();
        }

        Camera? camera = await _access
            .FindAsync(uuid, userId.Value, CameraOperation.ViewCamera, cancellationToken)
            .ConfigureAwait(false);

        // Not found and not permitted are deliberately the same answer, following
        // PrinterAccessService: a UUID that answers differently is a way to learn which exist.
        if (camera is null)
        {
            return Problem(
                title: "No such camera.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Asking is what schedules the next capture. Ordered before the read so that the very first
        // request on a cold camera starts the work rather than only discovering there is none.
        _frames.RequestRefresh(camera.Id, _streamServer.FrameUrl(camera.Uuid));

        CameraFrame? frame = _frames.Current(camera.Id);
        if (frame is null)
        {
            return NoContent();
        }

        // No caching anywhere: this resource is different every couple of seconds, and a cached
        // frame is the stale picture the age rule exists to prevent - reintroduced by a proxy.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers["X-Frame-Captured-At"] =
            frame.CapturedAt.ToString("O", CultureInfo.InvariantCulture);

        return File(frame.Bytes, frame.ContentType);
    }

    private long? UserId()
    {
        return long.TryParse(_userManager.GetUserId(User), CultureInfo.InvariantCulture, out long id)
            ? id
            : null;
    }
}
