using System.Net.Mime;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Homespool.Host.Controllers;

/// <summary>
/// A failure answered as <see cref="ProblemDetails"/>, typed by its status code so that naming it in
/// a <see cref="Results{TResult1, TResult2}"/> union documents it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist when <see cref="TypedResults"/> already has <see cref="TypedResults.NotFound{TValue}"/>
/// and <see cref="TypedResults.Problem(ProblemDetails)"/>.</b> The two halves of the framework's answer
/// do not meet: <c>NotFound&lt;ProblemDetails&gt;</c> documents a 404 but writes and documents it as
/// <c>application/json</c>, and <see cref="ProblemHttpResult"/> writes <c>application/problem+json</c>
/// but documents nothing, because its status is decided at run time. An arm here does both - it
/// carries the status in its type, so the OpenAPI document gets the code, the schema and the real
/// content type, and it executes as a <see cref="ProblemHttpResult"/>, so the wire gets what the
/// document says.
/// </para>
/// <para>
/// Built through <see cref="ControllerBase.ProblemDetailsFactory"/> rather than by hand, so
/// <c>type</c>, <c>title</c> and <c>traceId</c> come out the way every other problem response on the
/// host does. The factories are the extension methods below; the constructors are internal so an
/// arm's status and its body's <c>status</c> cannot disagree.
/// </para>
/// </remarks>
public abstract class ProblemResult : IResult, IStatusCodeHttpResult, IValueHttpResult, IValueHttpResult<ProblemDetails>
{
    private protected ProblemResult(ProblemDetails details)
    {
        Value = details;
    }

    /// <summary>The body, exactly as it will be written.</summary>
    public ProblemDetails Value { get; }

    public int? StatusCode => Value.Status;

    object? IValueHttpResult.Value => Value;

    public Task ExecuteAsync(HttpContext httpContext)
    {
        return TypedResults.Problem(Value).ExecuteAsync(httpContext);
    }

    /// <summary>What every arm documents: its own status, a <see cref="ProblemDetails"/> body, as problem JSON.</summary>
    private protected static void Document(EndpointBuilder builder, int statusCode)
    {
        builder.Metadata.Add(new ProducesResponseTypeMetadata(statusCode, typeof(ProblemDetails), [MediaTypeNames.Application.ProblemJson]));
    }
}

/// <summary>A 400 with a <see cref="ProblemDetails"/> body.</summary>
public sealed class BadRequestProblem : ProblemResult, IEndpointMetadataProvider
{
    internal BadRequestProblem(ProblemDetails details)
        : base(details)
    {
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        Document(builder, StatusCodes.Status400BadRequest);
    }
}

/// <summary>A 403 with a <see cref="ProblemDetails"/> body.</summary>
public sealed class ForbiddenProblem : ProblemResult, IEndpointMetadataProvider
{
    internal ForbiddenProblem(ProblemDetails details)
        : base(details)
    {
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        Document(builder, StatusCodes.Status403Forbidden);
    }
}

/// <summary>A 404 with a <see cref="ProblemDetails"/> body.</summary>
public sealed class NotFoundProblem : ProblemResult, IEndpointMetadataProvider
{
    internal NotFoundProblem(ProblemDetails details)
        : base(details)
    {
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        Document(builder, StatusCodes.Status404NotFound);
    }
}

/// <summary>A 409 with a <see cref="ProblemDetails"/> body.</summary>
public sealed class ConflictProblem : ProblemResult, IEndpointMetadataProvider
{
    internal ConflictProblem(ProblemDetails details)
        : base(details)
    {
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        Document(builder, StatusCodes.Status409Conflict);
    }
}

/// <summary>A 413 with a <see cref="ProblemDetails"/> body.</summary>
public sealed class PayloadTooLargeProblem : ProblemResult, IEndpointMetadataProvider
{
    internal PayloadTooLargeProblem(ProblemDetails details)
        : base(details)
    {
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        Document(builder, StatusCodes.Status413PayloadTooLarge);
    }
}

/// <summary>A 500 with a <see cref="ProblemDetails"/> body.</summary>
public sealed class InternalServerErrorProblem : ProblemResult, IEndpointMetadataProvider
{
    internal InternalServerErrorProblem(ProblemDetails details)
        : base(details)
    {
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        Document(builder, StatusCodes.Status500InternalServerError);
    }
}

/// <summary>A 502 with a <see cref="ProblemDetails"/> body.</summary>
public sealed class BadGatewayProblem : ProblemResult, IEndpointMetadataProvider
{
    internal BadGatewayProblem(ProblemDetails details)
        : base(details)
    {
    }

    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        Document(builder, StatusCodes.Status502BadGateway);
    }
}

/// <summary>
/// The factories: one per <see cref="ProblemResult"/> arm, named for it, each taking the one sentence
/// the caller is owed.
/// </summary>
/// <remarks>
/// <c>[ApiController]</c> turns a bare <c>NotFound()</c> into <see cref="ProblemDetails"/> on its own,
/// but that is an MVC filter and an <see cref="IResult"/> goes past it - so here every failure carries
/// its body explicitly, and a bare <see cref="TypedResults.NotFound()"/> would be the one shape a
/// caller has not been promised.
/// </remarks>
internal static class ProblemResults
{
    public static BadRequestProblem BadRequestProblem(this ControllerBase controller, string detail)
    {
        return new(controller.Details(StatusCodes.Status400BadRequest, detail));
    }

    public static ForbiddenProblem ForbiddenProblem(this ControllerBase controller, string detail)
    {
        return new(controller.Details(StatusCodes.Status403Forbidden, detail));
    }

    /// <summary>
    /// An authenticated principal that resolves to no account. <c>[Authorize]</c> should make this
    /// unreachable; it fails closed rather than acting on an invented id.
    /// </summary>
    public static ForbiddenProblem NoAccount(this ControllerBase controller)
    {
        return new(controller.Details(StatusCodes.Status403Forbidden, "This credential names no account."));
    }

    public static NotFoundProblem NotFoundProblem(this ControllerBase controller, string? detail = null)
    {
        return new(controller.Details(StatusCodes.Status404NotFound, detail));
    }

    public static ConflictProblem ConflictProblem(this ControllerBase controller, string detail)
    {
        return new(controller.Details(StatusCodes.Status409Conflict, detail));
    }

    public static PayloadTooLargeProblem PayloadTooLargeProblem(this ControllerBase controller, string detail)
    {
        return new(controller.Details(StatusCodes.Status413PayloadTooLarge, detail));
    }

    public static InternalServerErrorProblem InternalServerErrorProblem(this ControllerBase controller, string detail)
    {
        return new(controller.Details(StatusCodes.Status500InternalServerError, detail));
    }

    public static BadGatewayProblem BadGatewayProblem(this ControllerBase controller, string detail)
    {
        return new(controller.Details(StatusCodes.Status502BadGateway, detail));
    }

    /// <summary>
    /// A command the printer refused, or that never completed: 409, carrying which command it was
    /// and - when the printer answered rather than went missing - what it answered.
    /// </summary>
    /// <remarks>
    /// <c>command</c> and <c>outcome</c> ride as problem extensions, which is where
    /// <see href="https://www.rfc-editor.org/rfc/rfc9457">RFC 9457</see> puts members beyond the
    /// standard five. That keeps the response one shape whether the printer refused or never
    /// answered - the caller reads <c>detail</c> either way and the extensions only add detail for
    /// machines.
    /// </remarks>
    public static ConflictProblem CommandRefused(this ControllerBase controller,
                                                 string wireName,
                                                 string detail,
                                                 string? outcome = null)
    {
        ProblemDetails problem = controller.Details(StatusCodes.Status409Conflict, detail);

        problem.Extensions["command"] = wireName;

        if (outcome is not null)
        {
            problem.Extensions["outcome"] = outcome;
        }

        return new(problem);
    }

    /// <summary>
    /// A command the printer answered in a way this server could not use: 502, since that is the
    /// gateway's failure and not the caller's. Carries the command like <see cref="CommandRefused"/>.
    /// </summary>
    public static BadGatewayProblem CommandAnswerUnusable(this ControllerBase controller, string wireName, string detail)
    {
        ProblemDetails problem = controller.Details(StatusCodes.Status502BadGateway, detail);

        problem.Extensions["command"] = wireName;

        return new(problem);
    }

    private static ProblemDetails Details(this ControllerBase controller, int statusCode, string? detail)
    {
        return controller.ProblemDetailsFactory.CreateProblemDetails(controller.HttpContext, statusCode: statusCode, detail: detail);
    }
}
