using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using Homespool.Host.Authorisation;
using Homespool.Host.Cameras;
using Homespool.Model;
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
    private readonly WebRtcAvailability _liveView;
    private readonly UserManager<HSUser> _userManager;

    public CameraController(CameraAccessService access,
                            CameraFrameCache frames,
                            Go2RtcClient streamServer,
                            WebRtcAvailability liveView,
                            UserManager<HSUser> userManager)
    {
        _access = access;
        _frames = frames;
        _streamServer = streamServer;
        _liveView = liveView;
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

    // The 200 is said here because a file result carries no metadata of its own; the rest of the
    // answers say theirs through the union.
    [Produces("image/jpeg")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<Results<FileContentHttpResult, NoContent, ForbiddenProblem, NotFoundProblem>> Frame(
        Guid uuid,
        CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return this.NoAccount();
        }

        Camera? camera = await _access
                               .FindAsync(uuid, CallerResolver.For(userId.Value, User), Capability.ViewCamera, cancellationToken)
                               .ConfigureAwait(false);

        // Not found and not permitted are deliberately the same answer, following
        // PrinterAccessService: a UUID that answers differently is a way to learn which exist.
        if (camera is null)
        {
            return this.NotFoundProblem("No such camera.");
        }

        // Asking is what schedules the next capture. Ordered before the read so that the very first
        // request on a cold camera starts the work rather than only discovering there is none.
        //
        // No address means the sidecar has no credential and is not used, so there is nothing to
        // schedule. The read below then finds no frame and answers 204 - the same answer a camera
        // that is switched off gives, which is honest: there is no current picture either way.
        if (_streamServer.FrameUrl(camera.Uuid) is { } frameUrl)
        {
            _frames.RequestRefresh(camera.Id, frameUrl);
        }

        CameraFrame? frame = _frames.Current(camera.Id);
        if (frame is null)
        {
            return TypedResults.NoContent();
        }

        // No caching anywhere: this resource is different every couple of seconds, and a cached
        // frame is the stale picture the age rule exists to prevent - reintroduced by a proxy.
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers["X-Frame-Captured-At"] =
            frame.CapturedAt.ToString("O", CultureInfo.InvariantCulture);

        return TypedResults.File(frame.Bytes, frame.ContentType);
    }

    /// <summary>
    /// Whether this camera can be watched live. <c>GET /api/v1/cameras/{uuid}/live</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked by the page rather than decided while it renders, and that is the whole point of it
    /// being an endpoint.</b> The answer depends on the codec the sidecar reports, and a stream it
    /// has not yet connected to reports none — so a page rendered a moment after the stack came up
    /// would decide "no" for a camera that can, and go on saying so until somebody reloaded. Asking
    /// separately means the button appears when it becomes true.
    /// </para>
    /// <para>
    /// <b>What it cannot do is tell you the codec in advance</b>, and that was measured rather than
    /// assumed: the stream server reports none for a camera nothing is watching, and 1.9.14 has no
    /// probe endpoint. So this answers optimistically - see <see cref="WebRtcAvailability.CanOffer"/>
    /// - and a camera whose video WebRTC cannot carry is learned from its first refusal.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Route("{uuid:guid}/live")]
    public async Task<Results<Ok<CameraLiveOption>, ForbiddenProblem, NotFoundProblem>> Live(
        Guid uuid,
        CancellationToken cancellationToken)
    {
        long? userId = UserId();
        if (userId is null)
        {
            return this.NoAccount();
        }

        Camera? camera = await _access
                               .FindAsync(uuid, CallerResolver.For(userId.Value, User), Capability.ViewCamera, cancellationToken)
                               .ConfigureAwait(false);

        if (camera is null)
        {
            return this.NotFoundProblem("No such camera.");
        }

        return TypedResults.Ok(new CameraLiveOption(_liveView.CanOffer(camera.Uuid)));
    }

    /// <summary>
    /// Exchanges a browser's WebRTC offer for the stream server's answer.
    /// <c>POST /api/v1/cameras/{uuid}/webrtc</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where the permission is spent, and it is the only place it can be.</b> The pictures
    /// do not come through here — media goes straight from the sidecar to the browser over its own
    /// port, which is the one thing about WebRTC that cannot be proxied. What passes through is the
    /// handshake that hands over the ICE credentials, so refusing here is refusing the stream, and a
    /// published media port on its own gives out nothing.
    /// </para>
    /// <para>
    /// <b>Availability is re-checked rather than trusted</b>, even though the page has asked already:
    /// this endpoint is reachable on its own, and an offer for a camera whose codec cannot be carried
    /// would otherwise be forwarded for the sidecar to refuse — which answers, correctly but
    /// unhelpfully, that the gateway is at fault.
    /// </para>
    /// <para>
    /// <b>The offer is not read.</b> It comes from a browser and goes to the sidecar, both of which
    /// understand it; parsing it here would only add a third opinion about a negotiation Homespool
    /// takes no part in.
    /// </para>
    /// </remarks>
    [HttpPost]
    [Route("{uuid:guid}/webrtc")]
    public async Task<Results<Ok<WebRtcDescription>, BadRequestProblem, ForbiddenProblem, NotFoundProblem,
        ConflictProblem, BadGatewayProblem>> WebRtc(
        Guid uuid,
        [FromBody] WebRtcDescription offer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        long? userId = UserId();
        if (userId is null)
        {
            return this.NoAccount();
        }

        if (string.IsNullOrWhiteSpace(offer.Sdp))
        {
            return this.BadRequestProblem("An offer with no session description cannot be answered.");
        }

        Camera? camera = await _access
                               .FindAsync(uuid, CallerResolver.For(userId.Value, User), Capability.ViewCamera, cancellationToken)
                               .ConfigureAwait(false);

        // The same answer for absent and forbidden, following Frame above and PrinterAccessService:
        // a UUID that answers differently is a way to learn which ones exist.
        if (camera is null)
        {
            return this.NotFoundProblem("No such camera.");
        }

        if (!_liveView.CanOffer(camera.Uuid))
        {
            return this.ConflictProblem("This camera cannot be watched live.");
        }

        WebRtcOffer answer = await _streamServer.OfferAsync(camera.Uuid, offer.Sdp, cancellationToken)
                                                .ConfigureAwait(false);

        // A permanent fact about the camera rather than a failure of this request, so it is
        // remembered: the page hides the button on this answer, and every later page never shows it.
        // This is the only moment a camera's codec can be learned - nothing reports it beforehand.
        if (answer.Outcome == WebRtcOfferOutcome.CodecUnsupported)
        {
            _liveView.MarkUnsupported(camera.Uuid);

            return this.ConflictProblem("This camera cannot be watched live.");
        }

        if (answer.Outcome != WebRtcOfferOutcome.Answered || answer.Sdp is null)
        {
            return this.BadGatewayProblem("The stream server did not answer the offer.");
        }

        return TypedResults.Ok(new WebRtcDescription("answer", answer.Sdp));
    }

    private long? UserId()
    {
        return long.TryParse(_userManager.GetUserId(User), CultureInfo.InvariantCulture, out long id) ? id : null;
    }
}
