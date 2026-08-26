using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

namespace Homespool.Host.Services;

/// <summary>
/// Turns "the client hung up" into 499 rather than letting it escape as a 500.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cancelled request is not a server fault, and it was being logged as one.</b> A browser that
/// navigates away, reloads, or simply gives up mid-request aborts it; whatever was in flight throws
/// <see cref="OperationCanceledException"/> from the first thing that checks the token - usually
/// opening a database connection - and with nothing catching it that surfaced as an unhandled
/// exception, a 500, and a full stack trace in the log.
/// </para>
/// <para>
/// <b>Found on the appliance</b> (2026-08-23): the front page polls itself, the Pi 3 is slow while it
/// warms up after a deploy, and every poll abandoned during that window left an error with a stack
/// trace. Nothing was broken and nothing on screen suffered - <c>live-region.js</c> keeps its last
/// good content through a failed refresh - so the whole cost was noise in the log, in exactly the
/// place somebody reads during an incident.
/// </para>
/// <para>
/// <b>Middleware rather than a catch in each polled handler.</b> There are four of them across two
/// pages, all polling on a timer, and any endpoint has the same exposure - this is a fact about the
/// request's lifetime rather than about what any handler does.
/// </para>
/// <para>
/// <b>Nothing is written to the client, and there is nobody to write it to.</b> 499 is nginx's
/// invention, never standardised and absent from IANA's registry, though ASP.NET Core carries the
/// constant. Its entire job here is to tell the log what happened. Do not reach for it as a reply.
/// </para>
/// </remarks>
public sealed class ClientGoneMiddleware : IMiddleware
{
    /// <inheritdoc />
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The guard is the whole safety of this. A cancellation that is NOT this request's abort
            // is something else entirely - the host shutting down, or a timeout somebody meant - and
            // swallowing those would hide a real failure behind a status nobody reads as one.
            //
            // Setting the status is best-effort: if the response has started there is nothing to
            // change, and the log takes whatever was already sent.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }
        }
    }
}
