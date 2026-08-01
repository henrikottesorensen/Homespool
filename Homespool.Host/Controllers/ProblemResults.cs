using Microsoft.AspNetCore.Mvc;

namespace Homespool.Host.Controllers;

/// <summary>
/// Failure responses as <see cref="ProblemDetails"/>, which is what the API already claims to
/// return.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written after reading the generated OpenAPI document, not before.</b> A
/// <c>[ProducesResponseType]</c> with no type does not mean "no body": for a client-error code the
/// generator fills in <c>ProblemDetails</c> from <c>ApiBehaviorOptions</c>. So documenting the codes
/// made the document assert a shape that four-fifths of these responses did not have - anonymous
/// objects and bare strings - and a generated client would have deserialised a 409 into an empty
/// <c>ProblemDetails</c>. The honest fix is to return what is claimed.
/// </para>
/// <para>
/// The bare helpers already do: <c>[ApiController]</c> turns <c>NotFound()</c>, <c>Conflict()</c> and
/// friends into <c>ProblemDetails</c> when they carry no body of their own. It is only the ones given
/// a body that diverged, so this closes a split that had grown by accident rather than by decision.
/// </para>
/// <para>
/// Built through <see cref="ControllerBase.ProblemDetailsFactory"/> rather than by hand, so
/// <c>type</c>, <c>title</c> and <c>traceId</c> come out the way every other problem response on the
/// host does.
/// </para>
/// </remarks>
internal static class ProblemResults
{
    /// <summary>A failure with something to say to the caller, and nothing machine-readable to add.</summary>
    public static ObjectResult Failure(this ControllerBase controller, int statusCode, string detail)
    {
        ProblemDetails problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext, statusCode: statusCode, detail: detail);

        return new ObjectResult(problem) { StatusCode = statusCode };
    }

    /// <summary>
    /// A failure caused by a command to a printer, carrying which command it was and - when the
    /// printer answered rather than went missing - what it answered.
    /// </summary>
    /// <remarks>
    /// <c>command</c> and <c>outcome</c> ride as problem extensions, which is where
    /// <see href="https://www.rfc-editor.org/rfc/rfc9457">RFC 9457</see> puts members beyond the
    /// standard five. That keeps the response one shape whether the printer refused, never answered,
    /// or answered unreadably - the caller reads <c>detail</c> either way and the extensions only add
    /// detail for machines.
    /// </remarks>
    public static ObjectResult CommandFailure(this ControllerBase controller, int statusCode, string wireName,
                                              string detail, string? outcome = null)
    {
        ProblemDetails problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext, statusCode: statusCode, detail: detail);

        problem.Extensions["command"] = wireName;

        if (outcome is not null)
        {
            problem.Extensions["outcome"] = outcome;
        }

        return new ObjectResult(problem) { StatusCode = statusCode };
    }
}
