using System;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Homespool.Host.Controllers;

/// <summary>
/// A bodiless status code that documents itself — the bare-status counterpart to
/// <see cref="ProblemResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>TypedResults.StatusCode(…)</c> answers with
/// <see cref="Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult"/>, whose status is decided
/// at run time and which therefore adds nothing to the document — so an action returning one has to
/// restate the status in a <c>[ProducesResponseType]</c>. That is two statements which can disagree,
/// and removing exactly that hazard is why the union rule exists at all
/// (<c>AGENT-NOTES.md</c> §7, <c>notes/typed-results.md</c>). Putting the status in the type closes
/// it: the arm and the document cannot drift, because they are the same fact.
/// </para>
/// <para>
/// <b>Generic, where <see cref="ProblemResult"/> is seven concrete arms.</b> Those differ in more
/// than their status — each carries a <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> body — so
/// a type each earns its place. A bodiless status differs in nothing but the number, and a concrete
/// type per status would be a file of near-identical classes.
/// </para>
/// <para>
/// <b>It implements <see cref="IStatusCodeHttpResult"/></b> for the same reason
/// <see cref="ProblemResult"/> does: it is how anything inspecting a result — the test suites
/// included — reads the status without executing it.
/// </para>
/// </remarks>
/// <typeparam name="TStatus">The status, as a type. See <see cref="Status"/>.</typeparam>
public sealed class StatusCodeResult<TStatus> : IResult, IStatusCodeHttpResult, IEndpointMetadataProvider
    where TStatus : IStatusCode
{
    /// <inheritdoc />
    public int? StatusCode => TStatus.Code;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Response.StatusCode = TStatus.Code;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Documents the status, and nothing about a body — there is not one.
    /// </summary>
    /// <remarks>
    /// No content type either. A bodiless response that advertised one would be the same kind of lie
    /// the attribute era told, one field further down.
    /// </remarks>
    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(TStatus.Code));
    }
}

/// <summary>
/// A status code carried in a type, so a result can say which one it is.
/// </summary>
/// <remarks>
/// A <c>static abstract</c> member rather than a constructor argument, because the whole point is
/// that the value is available without an instance: <see cref="IEndpointMetadataProvider"/> is
/// static, and a status only reachable through an instance cannot reach the document.
/// </remarks>
public interface IStatusCode
{
    /// <summary>The status this type stands for.</summary>
    /// <remarks>
    /// Named <c>Code</c> rather than <c>StatusCode</c> so it cannot be read as the instance property
    /// of the same name that <see cref="IStatusCodeHttpResult"/> puts on the result itself. One is a
    /// fact about the type; the other is what a caller asks an instance.
    /// </remarks>
    static abstract int Code { get; }
}

/// <summary>
/// The statuses <see cref="StatusCodeResult{TStatus}"/> can be asked for. One type per status, and
/// only the ones actually returned — an unused marker is a type nobody can find a reason for.
/// </summary>
public static class Status
{
    /// <summary>416, for a <c>Range</c> this server will not serve.</summary>
    public readonly struct RangeNotSatisfiable : IStatusCode
    {
        /// <inheritdoc />
        public static int Code => StatusCodes.Status416RangeNotSatisfiable;
    }
}
