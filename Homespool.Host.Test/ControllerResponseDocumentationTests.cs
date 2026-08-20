using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

using Homespool.Host.Controllers;

namespace Homespool.Host.Test;

/// <summary>
/// That every app-API action's answers reach the OpenAPI document, and that every failure it
/// documents is a <see cref="ProblemDetails"/> as problem JSON - which is what these endpoints
/// return.
/// </summary>
/// <remarks>
/// <para>
/// Companion to <see cref="ControllerReturnTypeTests"/>, and enforced for the same reason: the cost
/// of forgetting is a quietly thinner document, which nothing else would notice.
/// </para>
/// <para>
/// <b>The document is read off the types, the way ApiExplorer reads it.</b> An arm of a
/// <see cref="Results{TResult1, TResult2}"/> union documents itself through
/// <see cref="IEndpointMetadataProvider"/>, so these tests ask each arm what it would say rather than
/// looking for an attribute that restates it. The attribute remains for what a type cannot say - a
/// file result's 200 - and an arm that says nothing on its own is only allowed where one is present.
/// </para>
/// <para>
/// <b>Scoped to the app API.</b> <see cref="PrusaConnectPrinterController"/> is excluded - <c>/p/*</c>
/// is Prusa's protocol rather than ours, its only clients are printers running firmware that was
/// written against Connect, and nobody will ever read our OpenAPI document to implement against it.
/// So is <see cref="OctoPrintCompatController"/>, which is hidden from the document on purpose and
/// answers its failures as prose.
/// </para>
/// </remarks>
public class ControllerResponseDocumentationTests
{
    private static IEnumerable<Type> AppApiControllers =>
    [
        typeof(PrinterController),
        typeof(PrintFileController),
        typeof(PrinterAppController),
        typeof(PrintQueueController),
        typeof(CameraController),
    ];

    private static IEnumerable<MethodInfo> Actions(Type controller)
    {
        return controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(method => !method.IsSpecialName
                                          && method.GetCustomAttribute<NonActionAttribute>() is null);
    }

    private static Type Unwrapped(Type returnType)
    {
        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>) ?
            returnType.GetGenericArguments()[0] :
            returnType;
    }

    /// <summary>The arms of a union, or the one type when the action has a single answer.</summary>
    private static IEnumerable<Type> Arms(MethodInfo action)
    {
        Type returned = Unwrapped(action.ReturnType);

        return returned.IsGenericType && returned.Namespace == typeof(Results<,>).Namespace
                                     && returned.GetGenericTypeDefinition().Name.StartsWith("Results`", StringComparison.Ordinal) ?
            returned.GetGenericArguments() :
            [returned];
    }

    /// <summary>What one arm tells ApiExplorer about itself - nothing, for a type that cannot say.</summary>
    private static IReadOnlyList<IProducesResponseTypeMetadata> Documented(Type arm, MethodInfo action)
    {
        if (!typeof(IEndpointMetadataProvider).IsAssignableFrom(arm))
        {
            return [];
        }

        RouteEndpointBuilder builder = new(null, RoutePatternFactory.Parse("/"), 0);

        arm.GetInterfaceMap(typeof(IEndpointMetadataProvider)).TargetMethods[0].Invoke(null, [action, builder]);

        return builder.Metadata.OfType<IProducesResponseTypeMetadata>().ToList();
    }

    /// <summary>
    /// Every arm reaches the document: it documents itself, or the action carries an attribute for
    /// what the type cannot say.
    /// </summary>
    [Fact]
    public void EveryAppApiAnswerReachesTheDocument()
    {
        // Act
        List<string> silent = (from controller in AppApiControllers
            from action in Actions(controller)
            from arm in Arms(action)
            where Documented(arm, action).Count == 0
                  && !action.GetCustomAttributes<ProducesResponseTypeAttribute>().Any()
            select $"{controller.Name}.{action.Name} answers {arm.Name}, which documents nothing, "
                   + "and the action carries no [ProducesResponseType] to say it").ToList();

        // Assert
        silent.Should().BeEmpty("an endpoint's answers are part of its contract");
    }

    /// <summary>
    /// Every documented failure says <see cref="ProblemDetails"/>, as <c>application/problem+json</c>,
    /// because that is what these endpoints return.
    /// </summary>
    /// <remarks>
    /// The rule exists because the document said it first. A <c>[ProducesResponseType]</c> with no
    /// type is not "no body" - for a client-error code the generator fills in <c>ProblemDetails</c>
    /// from <c>ApiBehaviorOptions</c>, so documenting the codes silently asserted a shape the
    /// anonymous-object bodies did not have. A <see cref="ProblemResult"/> arm says the type and the
    /// content type together, and this test means a new endpoint cannot quietly reintroduce a second
    /// error shape - not through a framework arm such as <see cref="NotFound"/>, which would document
    /// an empty 404, and not through an attribute.
    /// </remarks>
    [Fact]
    public void EveryDocumentedFailureIsAProblemDetailsAsProblemJson()
    {
        // Act
        List<string> fromArms = (from controller in AppApiControllers
            from action in Actions(controller)
            from arm in Arms(action)
            from response in Documented(arm, action)
            where response.StatusCode >= 400
                  && (response.Type != typeof(ProblemDetails)
                      || !response.ContentTypes.SequenceEqual([MediaTypeNames.Application.ProblemJson]))
            select $"{controller.Name}.{action.Name} documents {response.StatusCode} through {arm.Name} "
                   + $"as {response.Type?.Name ?? "nothing"} [{string.Join(", ", response.ContentTypes)}]").ToList();

        List<string> fromAttributes = (from controller in AppApiControllers
            from action in Actions(controller)
            from attribute in action.GetCustomAttributes<ProducesResponseTypeAttribute>()
            where attribute.StatusCode >= 400 && attribute.Type != typeof(ProblemDetails)
            select $"{controller.Name}.{action.Name} documents {attribute.StatusCode} "
                   + $"as {attribute.Type.Name}").ToList();

        // Assert
        fromArms.Should().BeEmpty();
        fromAttributes.Should().BeEmpty();
    }

    /// <summary>
    /// The arms really do say what the tests above rely on them saying - pinned here so that a
    /// framework change to what an arm documents fails this file rather than thinning the document.
    /// </summary>
    [Fact]
    public void TheArmsDocumentWhatTheyExecute()
    {
        MethodInfo anyAction = Actions(typeof(PrinterController)).First();

        IProducesResponseTypeMetadata notFound = Documented(typeof(NotFoundProblem), anyAction).Should().ContainSingle().Subject;
        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        notFound.Type.Should().Be(typeof(ProblemDetails));
        notFound.ContentTypes.Should().Equal(MediaTypeNames.Application.ProblemJson);

        IProducesResponseTypeMetadata ok = Documented(typeof(Ok<string>), anyAction).Should().ContainSingle().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Type.Should().Be(typeof(string));

        Documented(typeof(FileStreamHttpResult), anyAction).Should().BeEmpty("a file result cannot say its content type, "
                                                                          + "which is why the attribute survives there");

        Documented(typeof(NotFound), anyAction).Should().ContainSingle()
                                               .Which.Type.Should().NotBe(typeof(ProblemDetails),
                                                                          "the framework's bare 404 is the shape the rule refuses");
    }
}
