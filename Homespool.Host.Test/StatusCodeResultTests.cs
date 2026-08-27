using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

using Homespool.Host.Controllers;

namespace Homespool.Host.Test;

/// <summary>
/// That a bare status code documents itself, which is the only reason this type exists.
/// </summary>
/// <remarks>
/// <para>
/// <c>TypedResults.StatusCode(…)</c> decides its status at run time and so adds nothing to the
/// document, leaving the action to restate it in a <c>[ProducesResponseType]</c> — two statements
/// that can disagree, which is the hazard the union rule removes. Putting the status in the type
/// makes the arm and the document the same fact.
/// </para>
/// <para>
/// The metadata is read the way <c>ControllerResponseDocumentationTests</c> reads it, and the way
/// ApiExplorer does: ask the type to populate a builder, then look at what it added.
/// </para>
/// </remarks>
public class StatusCodeResultTests
{
    [Fact]
    public void TheStatusIsAvailableWithoutExecutingTheResult()
    {
        StatusCodeResult<Status.RangeNotSatisfiable> result = new();

        result.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable,
                                      "anything inspecting a result reads it through IStatusCodeHttpResult");
    }

    [Fact]
    public async Task ExecutingItSetsThatStatusAndWritesNoBody()
    {
        DefaultHttpContext context = new();

        await new StatusCodeResult<Status.RangeNotSatisfiable>().ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
        context.Response.ContentType.Should().BeNull("a bare status has no body to describe");
    }

    /// <summary>
    /// The point of the type. Without this the status reaches the document only through an attribute,
    /// and nothing notices when the two drift apart.
    /// </summary>
    [Fact]
    public void ItDocumentsItsOwnStatus()
    {
        RouteEndpointBuilder builder = new(null, RoutePatternFactory.Parse("/"), 0);

        typeof(StatusCodeResult<Status.RangeNotSatisfiable>)
            .GetInterfaceMap(typeof(IEndpointMetadataProvider))
            .TargetMethods[0]
            .Invoke(null, [typeof(StatusCodeResultTests).GetMethod(nameof(ItDocumentsItsOwnStatus))!, builder]);

        builder.Metadata.OfType<IProducesResponseTypeMetadata>()
               .Select(response => response.StatusCode)
               .Should().ContainSingle()
               .Which.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
    }

    /// <summary>
    /// The status comes from the type argument, not from the class - so a second status is a second
    /// marker and nothing else.
    /// </summary>
    [Fact]
    public void ADifferentMarkerIsADifferentStatus()
    {
        new StatusCodeResult<TeapotForTesting>().StatusCode.Should().Be(418);
    }

    private readonly struct TeapotForTesting : IStatusCode
    {
        public static int Code => 418;
    }
}
